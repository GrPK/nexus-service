// Streamed panels: the macOS counterpart of nexus-overlay's StreamPanelHost.
//
// One borderless window per stream session, parked off every display, renders
// /panel/{id} in a WKWebView. ScreenCaptureKit captures that window at the
// session's pixel size and fps, change-driven like Windows.Graphics.Capture,
// and each complete frame goes to POST /panel/streams/{sessionId}/ingest as raw
// BGRA. The service owns pacing and the device transport. Reconciled against
// GET /panel/streams/assignments on a poll and on the service's stdin poke.

import AppKit
import WebKit
import ScreenCaptureKit
import CoreMedia
import CoreVideo
import Network

func streamLog(_ message: String) {
    FileHandle.standardError.write("[stream-host] \(message)\n".data(using: .utf8)!)
}

struct StreamAssignment {
    let sessionId: String
    let panelDeviceId: String
    let cssWidth: Int
    let cssHeight: Int
    let dpr: Double
    let fps: Int
    let codec: String
}

final class StreamHostController {
    /// Same cadence as nexus-overlay's state poll on Windows.
    private static let pollInterval: TimeInterval = 5
    /// Holds off respawns once Screen Recording is refused, so a missing grant is not retried every poll.
    private static let deniedBackoff: TimeInterval = 60

    private let opts: Options
    private var hosts: [String: StreamPanelHost] = [:]
    private var fetchInFlight = false
    private var fetchQueued = false
    private var deniedUntil = Date.distantPast
    private var unsupportedLogged = Set<String>()

    init(_ opts: Options) { self.opts = opts }

    func start() {
        refresh()
        Timer.scheduledTimer(withTimeInterval: Self.pollInterval, repeats: true) { [weak self] _ in
            self?.refresh()
        }
    }

    /// Fetch + reconcile, coalesced like KioskController.refresh. Main thread only.
    func refresh() {
        if fetchInFlight { fetchQueued = true; return }
        fetchInFlight = true
        fetchAssignments { [weak self] items in
            DispatchQueue.main.async {
                guard let self = self else { return }
                self.fetchInFlight = false
                // A failed fetch keeps the running hosts; the next poll retries.
                if let items = items { self.reconcile(items) }
                if self.fetchQueued {
                    self.fetchQueued = false
                    self.refresh()
                }
            }
        }
    }

    private func fetchAssignments(_ completion: @escaping ([StreamAssignment]?) -> Void) {
        var c = URLComponents()
        c.scheme = "http"
        c.host = "localhost"
        c.port = opts.port
        c.path = "/panel/streams/assignments"
        c.queryItems = [URLQueryItem(name: "token", value: opts.token)]
        guard let url = c.url else { completion(nil); return }
        URLSession.shared.dataTask(with: url) { data, response, _ in
            guard let data = data,
                  let http = response as? HTTPURLResponse, http.statusCode == 200,
                  let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
                  let list = obj["assignments"] as? [[String: Any]]
            else { completion(nil); return }
            let items: [StreamAssignment] = list.compactMap { e in
                guard let sessionId = e["sessionId"] as? String, !sessionId.isEmpty,
                      let deviceId = e["panelDeviceId"] as? String, !deviceId.isEmpty,
                      let width = e["cssWidth"] as? Int, let height = e["cssHeight"] as? Int
                else { return nil }
                return StreamAssignment(
                    sessionId: sessionId,
                    panelDeviceId: deviceId,
                    cssWidth: width,
                    cssHeight: height,
                    dpr: (e["dpr"] as? Double) ?? 1,
                    fps: (e["fps"] as? Int) ?? 30,
                    codec: (e["codec"] as? String) ?? "h264")
            }
            completion(items)
        }.resume()
    }

    private func reconcile(_ assignments: [StreamAssignment]) {
        var desired: [String: StreamAssignment] = [:]
        for a in assignments { desired[a.sessionId] = a }

        for (sessionId, host) in hosts where desired[sessionId] == nil {
            hosts[sessionId] = nil
            host.close()
            streamLog("closed session=\(sessionId) (unassigned)")
        }

        guard Date() >= deniedUntil else { return }
        for sessionId in desired.keys.sorted() where hosts[sessionId] == nil {
            let a = desired[sessionId]!
            // Every USB screen takes raw frames; only the dev-tools D213 board asks for H.264.
            guard a.codec.caseInsensitiveCompare("rawBgra") == .orderedSame else {
                if unsupportedLogged.insert(sessionId).inserted {
                    streamLog("session=\(sessionId) codec=\(a.codec) is not streamed on macOS")
                }
                continue
            }
            let host = StreamPanelHost(a, opts)
            host.onFault = { [weak self, weak host] denied in
                guard let self = self, let host = host else { return }
                if self.hosts[sessionId] === host { self.hosts[sessionId] = nil }
                if denied { self.deniedUntil = Date().addingTimeInterval(Self.deniedBackoff) }
                streamLog("faulted session=\(sessionId)")
            }
            hosts[sessionId] = host
            host.start()
        }
    }
}

final class StreamPanelHost: NSObject, WKNavigationDelegate, SCStreamOutput, SCStreamDelegate {
    private static let offscreenOrigin: CGFloat = -20000
    /// ScreenCaptureKit delivers an idle callback every frame interval, so this long without one is a dead capture.
    private static let stallTimeout: TimeInterval = 5
    /// Surfaces ScreenCaptureKit may have out at once: ingest holds up to its in-flight cap zero-copy.
    private static let captureQueueDepth = 5

    let sessionId: String
    /// Fires once on the main thread when the host dies on its own; true when Screen Recording is not granted.
    var onFault: ((Bool) -> Void)?

    private let assignment: StreamAssignment
    private let opts: Options
    private let pixelWidth: Int
    private let pixelHeight: Int
    private let fps: Int
    private let window: NSWindow
    private let webView: WKWebView
    // Capture callbacks, ingest sends and their completions all run here, so frame state needs no lock.
    private let queue: DispatchQueue

    private var stream: SCStream?
    private var captureStarted = false
    private var closed = false

    // queue-confined
    private var ingest: IngestConnection?
    private var watchdog: DispatchSourceTimer?
    private var lastCallbackNanos: UInt64 = 0
    private var firstFrameLogged = false

    init(_ assignment: StreamAssignment, _ opts: Options) {
        self.assignment = assignment
        self.opts = opts
        sessionId = assignment.sessionId
        fps = min(max(assignment.fps, 1), 240)
        // The ingest frame size is css * dpr, matching the overlay's even-dimension rule.
        pixelWidth = max(2, Int((Double(assignment.cssWidth) * assignment.dpr).rounded()) & ~1)
        pixelHeight = max(2, Int((Double(assignment.cssHeight) * assignment.dpr).rounded()) & ~1)
        queue = DispatchQueue(label: "stream-host-\(assignment.sessionId)", qos: .userInteractive)

        let rect = NSRect(x: Self.offscreenOrigin, y: Self.offscreenOrigin, width: CGFloat(pixelWidth), height: CGFloat(pixelHeight))
        let win = NSWindow(contentRect: rect, styleMask: [.borderless], backing: .buffered, defer: false)
        win.isReleasedWhenClosed = false
        win.ignoresMouseEvents = true
        win.hasShadow = false
        win.isExcludedFromWindowsMenu = true
        win.collectionBehavior = [.stationary, .ignoresCycle, .fullScreenNone]

        let view = WKWebView(frame: NSRect(origin: .zero, size: rect.size))
        // The window is sized in frame pixels; zooming by dpr lays the page out at its CSS size,
        // as the overlay's WebView2 RasterizationScale does.
        view.pageZoom = CGFloat(assignment.dpr)
        // WebKit stops painting a window it considers occluded, and an off-screen one always is.
        // KVC on a missing key raises an uncatchable exception, hence the check.
        if view.responds(to: NSSelectorFromString("_setWindowOcclusionDetectionEnabled:")) {
            view.setValue(false, forKey: "windowOcclusionDetectionEnabled")
        }
        win.contentView = view
        window = win
        webView = view
        super.init()
        webView.navigationDelegate = self
    }

    func start() {
        var c = URLComponents()
        c.scheme = "http"
        c.host = "localhost"
        c.port = opts.port
        c.path = "/panel/\(assignment.panelDeviceId)"
        // streamFps lets the page cap its shader draws near what the capture keeps.
        c.queryItems = [URLQueryItem(name: "token", value: opts.token), URLQueryItem(name: "streamFps", value: String(fps))]
        guard let url = c.url else { fault(denied: false); return }
        window.orderBack(nil)
        webView.load(URLRequest(url: url))
        streamLog("opened session=\(sessionId) device=\(assignment.panelDeviceId) \(pixelWidth)x\(pixelHeight)@\(fps) dpr=\(assignment.dpr)")
    }

    func close() {
        if closed { return }
        closed = true
        stream?.stopCapture(completionHandler: nil)
        stream = nil
        queue.async { [self] in
            watchdog?.cancel()
            watchdog = nil
            ingest?.cancel()
            ingest = nil
        }
        webView.navigationDelegate = nil
        webView.stopLoading()
        window.orderOut(nil)
        window.close()
    }

    private func fault(denied: Bool) {
        if closed { return }
        let callback = onFault
        close()
        callback?(denied)
    }

    // MARK: navigation

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        if closed || captureStarted { return }
        captureStarted = true
        startCapture()
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        streamLog("session=\(sessionId) navigation failed: \(error.localizedDescription)")
        fault(denied: false)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        streamLog("session=\(sessionId) navigation failed: \(error.localizedDescription)")
        fault(denied: false)
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        streamLog("session=\(sessionId) web content process terminated")
        fault(denied: false)
    }

    // MARK: capture

    private func startCapture() {
        let windowID = CGWindowID(window.windowNumber)
        SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: false) { [weak self] content, error in
            DispatchQueue.main.async { self?.beginCapture(content, error, windowID) }
        }
    }

    private func beginCapture(_ content: SCShareableContent?, _ error: Error?, _ windowID: CGWindowID) {
        if closed { return }
        guard let target = content?.windows.first(where: { $0.windowID == windowID }) else {
            if !CGPreflightScreenCaptureAccess() {
                // Lists Nexus under Screen Recording and shows the prompt; capture inherits the grant.
                _ = CGRequestScreenCaptureAccess()
                streamLog("session=\(sessionId) Screen Recording is not granted; enable Nexus in System Settings > Privacy & Security > Screen Recording")
                fault(denied: true)
            } else {
                streamLog("session=\(sessionId) own window not capturable: \(error?.localizedDescription ?? "not listed")")
                fault(denied: false)
            }
            return
        }

        let config = SCStreamConfiguration()
        config.width = pixelWidth
        config.height = pixelHeight
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.colorSpaceName = CGColorSpace.sRGB
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
        config.showsCursor = false
        config.queueDepth = Self.captureQueueDepth

        let ingest = IngestConnection(port: opts.port, sessionId: sessionId, token: opts.token, queue: queue)
        ingest.onFault = { [weak self] reason in
            DispatchQueue.main.async {
                guard let self = self else { return }
                streamLog("session=\(self.sessionId) ingest ended: \(reason)")
                self.fault(denied: false)
            }
        }
        queue.sync {
            self.ingest = ingest
            ingest.open()
        }

        let stream = SCStream(filter: SCContentFilter(desktopIndependentWindow: target), configuration: config, delegate: self)
        do {
            try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        } catch {
            streamLog("session=\(sessionId) addStreamOutput failed: \(error.localizedDescription)")
            fault(denied: false)
            return
        }
        self.stream = stream
        stream.startCapture { [weak self] error in
            DispatchQueue.main.async {
                guard let self = self, !self.closed else { return }
                if let error = error {
                    streamLog("session=\(self.sessionId) capture start failed: \(error.localizedDescription)")
                    self.fault(denied: !CGPreflightScreenCaptureAccess())
                    return
                }
                streamLog("session=\(self.sessionId) capture started")
                self.queue.async { self.armWatchdog() }
            }
        }
    }

    private func armWatchdog() {
        lastCallbackNanos = DispatchTime.now().uptimeNanoseconds
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + Self.stallTimeout, repeating: Self.stallTimeout)
        timer.setEventHandler { [weak self] in
            guard let self = self else { return }
            let silentNanos = DispatchTime.now().uptimeNanoseconds - self.lastCallbackNanos
            if silentNanos > UInt64(Self.stallTimeout * 1_000_000_000) {
                DispatchQueue.main.async {
                    streamLog("session=\(self.sessionId) capture stalled")
                    self.fault(denied: false)
                }
            }
        }
        timer.resume()
        watchdog = timer
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen else { return }
        lastCallbackNanos = DispatchTime.now().uptimeNanoseconds
        // Idle frames repeat the last content; only a complete frame carries new pixels.
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer),
              let ingest = ingest
        else { return }
        if !firstFrameLogged {
            firstFrameLogged = true
            streamLog("session=\(sessionId) first frame captured")
        }
        ingest.sendFrame(pixelBuffer)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        DispatchQueue.main.async {
            streamLog("session=\(self.sessionId) capture stopped: \(error.localizedDescription)")
            self.fault(denied: false)
        }
    }
}

/// One long-lived chunked POST to /panel/streams/{sessionId}/ingest. Each frame is
/// u32_le length | u8 flags | payload, matching nexus-service StreamFraming.cs, carried
/// as one HTTP chunk. Queue-confined to the host's queue.
final class IngestConnection {
    private static let flagKeyframe: UInt8 = 0x01
    private static let flagControl: UInt8 = 0x02
    private static let headerSize = 5
    /// Frames on the socket at once; a newer frame is dropped rather than queued behind them.
    private static let maxInFlight = 2
    private static let keepaliveInterval: TimeInterval = 5
    /// The service stopped reading if the socket takes nothing for this long.
    private static let sendStallTimeout: TimeInterval = 10
    private static let crlf = Data([0x0D, 0x0A])

    /// Fires once, on the queue, when the connection ends for any reason but cancel().
    var onFault: ((String) -> Void)?

    private let connection: NWConnection
    private let queue: DispatchQueue
    private let requestHead: Data
    private var keepalive: DispatchSourceTimer?
    private var inFlight = 0
    private var sentSinceTick = false
    private var lastCompletionNanos: UInt64 = 0
    private var ended = false

    init(port: Int, sessionId: String, token: String, queue: DispatchQueue) {
        self.queue = queue
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        connection = NWConnection(
            host: "127.0.0.1",
            port: NWEndpoint.Port(rawValue: UInt16(port)) ?? 9400,
            using: NWParameters(tls: nil, tcp: tcp))
        let path = sessionId.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? sessionId
        // The service accepts a service token only on a loopback connection with a local Host header.
        requestHead = Data(("POST /panel/streams/\(path)/ingest HTTP/1.1\r\n"
            + "Host: localhost:\(port)\r\n"
            + "Authorization: Bearer \(token)\r\n"
            + "Content-Type: application/octet-stream\r\n"
            + "Transfer-Encoding: chunked\r\n\r\n").utf8)
    }

    func open() {
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed(let error): self?.end("connection failed: \(error)")
            case .cancelled: self?.end("connection cancelled")
            default: break
            }
        }
        connection.start(queue: queue)
        lastCompletionNanos = DispatchTime.now().uptimeNanoseconds
        connection.send(content: requestHead, completion: .contentProcessed { [weak self] error in
            if let error = error { self?.end("request failed: \(error)") }
        })
        // A response arrives only when the service ends the request: 404 for a stale session, 200 on teardown.
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { [weak self] data, _, _, error in
            let status = data.flatMap { String(data: $0.prefix(12), encoding: .ascii) } ?? "\(String(describing: error))"
            self?.end("service ended the request (\(status))")
        }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + Self.keepaliveInterval, repeating: Self.keepaliveInterval)
        timer.setEventHandler { [weak self] in self?.onKeepaliveTick() }
        timer.resume()
        keepalive = timer
    }

    func cancel() {
        ended = true
        keepalive?.cancel()
        keepalive = nil
        connection.cancel()
    }

    func sendFrame(_ pixelBuffer: CVPixelBuffer) {
        if ended || inFlight >= Self.maxInFlight { return }
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        let rowBytes = width * 4
        let length = rowBytes * height
        guard CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly) == kCVReturnSuccess else { return }
        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else {
            CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly)
            return
        }
        let stride = CVPixelBufferGetBytesPerRow(pixelBuffer)
        let payload: Data
        if stride == rowBytes {
            // Zero-copy: the surface stays locked and retained until the stack is done with its bytes.
            let retained = Unmanaged.passRetained(pixelBuffer)
            payload = Data(bytesNoCopy: base, count: length, deallocator: .custom { _, _ in
                CVPixelBufferUnlockBaseAddress(retained.takeUnretainedValue(), .readOnly)
                retained.release()
            })
        } else {
            var packed = Data(count: length)
            packed.withUnsafeMutableBytes { dst in
                for y in 0..<height {
                    memcpy(dst.baseAddress! + y * rowBytes, base + y * stride, rowBytes)
                }
            }
            CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly)
            payload = packed
        }
        // Every raw frame stands alone, so each one is a keyframe.
        send(payload, flags: Self.flagKeyframe)
    }

    private func onKeepaliveTick() {
        if ended { return }
        let now = DispatchTime.now().uptimeNanoseconds
        let stallNanos = UInt64(Self.sendStallTimeout * 1_000_000_000)
        if inFlight > 0 && now - lastCompletionNanos > stallNanos {
            end("service stopped reading")
            return
        }
        // Anything sent since the last tick already proves liveness.
        if !sentSinceTick && inFlight == 0 {
            send(nil, flags: Self.flagControl)
        }
        sentSinceTick = false
    }

    private func send(_ payload: Data?, flags: UInt8) {
        let length = payload?.count ?? 0
        var prefix = Data(String(length + Self.headerSize, radix: 16).utf8)
        prefix.append(Self.crlf)
        let size = UInt32(length)
        prefix.append(contentsOf: [UInt8(size & 0xFF), UInt8((size >> 8) & 0xFF), UInt8((size >> 16) & 0xFF), UInt8(size >> 24), flags])
        inFlight += 1
        sentSinceTick = true
        connection.batch {
            connection.send(content: prefix, completion: .contentProcessed { _ in })
            if let payload = payload {
                connection.send(content: payload, completion: .contentProcessed { _ in })
            }
            connection.send(content: Self.crlf, completion: .contentProcessed { [weak self] error in
                guard let self = self else { return }
                self.inFlight -= 1
                self.lastCompletionNanos = DispatchTime.now().uptimeNanoseconds
                if let error = error { self.end("send failed: \(error)") }
            })
        }
    }

    private func end(_ reason: String) {
        if ended { return }
        ended = true
        keepalive?.cancel()
        keepalive = nil
        connection.cancel()
        onFault?(reason)
    }
}
