#version 330 core
out vec4 fragColor;
uniform vec2  u_resolution;
uniform float u_time;
uniform float u_speed;      // hint_range(-2.0, 2.0, 0.02) = 1.0
uniform float u_hue;        // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_colorize;   // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_saturation; // hint_range(0.0, 4.0, 0.01) = 1.0
uniform float u_contrast;   // hint_range(0.0, 4.0, 0.01) = 1.0
const float PI = 3.14159265;
const float S_SPEED = 1.0;
const float S_HUE = 0.0;
const float S_SAT = 1.0;
const float S_CON = 1.0;
uniform float u_bmv4inq741m_scale; // hint_range(1.0, 12.0, 0.1) = 4.0
uniform float u_bmv4inq741m_speed; // hint_range(0.0, 2.0, 0.01) = 0.5
uniform float u_bmv4inq741m_style; // hint_range(0.0, 1.0, 1.0) = 0.0
uniform float u_bmv4inq741n_count; // hint_range(1.0, 40.0, 0.1) = 8.0
uniform float u_bmv4inq741n_speed; // hint_range(-4.0, 4.0, 0.01) = 1.0
uniform float u_bmv4inq741n_blend; // hint_range(0.0, 5.0, 1.0) = 0.0
uniform float u_bmv4inq741n_mix; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bmv4inq741o_palette; // hint_range(0.0, 8.0, 1.0) = 3.0
uniform float u_bmv4inq741o_spread; // hint_range(0.0, 3.0, 0.01) = 1.0
uniform float u_bmv4inq741o_offset; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_bmv4inq741o_cycle; // hint_range(-2.0, 2.0, 0.01) = 0.0
uniform float u_bmv4inq741o_shade; // hint_range(0.0, 1.0, 0.01) = 0.6
float hash21(vec2 p) { p = fract(p * vec2(234.34, 435.345)); p += dot(p, p + 34.23); return fract(p.x * p.y); }
float blendV(float a, float b, float mode, float amt) {
  float r = a + b;
  if (mode > 0.5) r = a * b;
  if (mode > 1.5) r = max(a, b);
  if (mode > 2.5) r = min(a, b);
  if (mode > 3.5) r = abs(a - b);
  if (mode > 4.5) r = 1.0 - (1.0 - a) * (1.0 - b);
  return clamp(mix(a, r, amt), 0.0, 1.0);
}
vec3 presetPal(float k, float id) {
  vec3 a = vec3(0.5); vec3 b = vec3(0.5); vec3 c = vec3(1.0); vec3 d = vec3(0.0, 0.33, 0.67);
  if (id > 0.5) { d = vec3(0.0, 0.1, 0.2); }
  if (id > 1.5) { a = vec3(0.1, 0.4, 0.6); b = vec3(0.1, 0.3, 0.4); d = vec3(0.6, 0.5, 0.4); }
  if (id > 2.5) { a = vec3(0.5, 0.3, 0.7); b = vec3(0.5, 0.4, 0.3); d = vec3(0.0, 0.5, 0.25); }
  if (id > 3.5) { a = vec3(0.6, 0.25, 0.05); b = vec3(0.4, 0.25, 0.05); c = vec3(0.5); d = vec3(0.5); }
  if (id > 4.5) { a = vec3(0.3, 0.6, 0.2); b = vec3(0.3, 0.4, 0.2); c = vec3(1.0); d = vec3(0.2, 0.0, 0.5); }
  if (id > 5.5) { a = vec3(0.8, 0.5, 0.7); b = vec3(0.2, 0.3, 0.3); d = vec3(0.0, 0.33, 0.67); }
  if (id > 6.5) { a = vec3(0.6, 0.8, 0.95); b = vec3(0.4, 0.2, 0.05); c = vec3(0.5); d = vec3(0.5); }
  if (id > 7.5) { a = vec3(0.5); b = vec3(0.5); c = vec3(0.5); d = vec3(0.5); }
  return clamp(a + b * cos(6.2831853 * (c * k + d)), 0.0, 1.0);
}
vec2 uvCentered() {
  vec2 u = gl_FragCoord.xy / u_resolution;
  u.y = 1.0 - u.y;
  u = u * 2.0 - 1.0;
  u.x *= u_resolution.x / u_resolution.y;
  return u;
}
vec3 hsv2rgb(vec3 c) {
  vec4 K = vec4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
  vec3 p = abs(fract(c.xxx + K.xyz) * 6.0 - K.www);
  return c.z * mix(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}
vec3 finalize(vec3 col, float hue) {
  float luma = dot(max(col, 0.0), vec3(0.2126, 0.7152, 0.0722));
  vec3 blended = mix(col, luma * hsv2rgb(vec3(hue, 1.0, 1.0)) * 1.4, clamp(u_colorize, 0.0, 1.0));
  float bl = dot(max(blended, 0.0), vec3(0.2126, 0.7152, 0.0722));
  blended = mix(vec3(bl), blended, clamp(u_saturation * S_SAT, 0.0, 4.0));
  blended = (blended - 0.5) * clamp(u_contrast * S_CON, 0.0, 4.0) + 0.5;
  return max(blended, 0.0);
}
void main() {
  vec2 p0 = uvCentered();
  vec2 p = p0;
  float t = mod(u_time, 3600.0) * u_speed * S_SPEED;
  float hue = u_hue + S_HUE;
  float v = 0.0;
  vec3 col = vec3(0.0);
  { float f = 0.0;
    vec2 q = p * (u_bmv4inq741m_scale) * 0.5; vec2 id = floor(q); vec2 fq = fract(q); float d1 = 8.0; float d2 = 8.0;
    for (int j = -1; j <= 1; j++) {
      for (int i = -1; i <= 1; i++) {
        vec2 g = vec2(float(i), float(j));
        vec2 h = vec2(hash21(id + g), hash21(id + g + 17.3));
        float d = length(g + 0.5 + 0.4 * sin(t * (u_bmv4inq741m_speed) + 6.2831853 * h) - fq);
        if (d < d1) { d2 = d1; d1 = d; } else if (d < d2) { d2 = d; }
      }
    }
    f = (u_bmv4inq741m_style) < 0.5 ? clamp(d1, 0.0, 1.0) : clamp((d2 - d1) * 2.0, 0.0, 1.0);
    v = f; }
  { float f = 0.0;
    f = 0.5 + 0.5 * sin(length(p) * (u_bmv4inq741n_count) * PI - t * (u_bmv4inq741n_speed) * 2.0);
    v = blendV(v, f, u_bmv4inq741n_blend, u_bmv4inq741n_mix); }
  {
    col = presetPal(v * (u_bmv4inq741o_spread) + (u_bmv4inq741o_offset) + hue + t * (u_bmv4inq741o_cycle) * 0.1, (u_bmv4inq741o_palette)) * mix(1.0, v, (u_bmv4inq741o_shade));
  }
  fragColor = vec4(finalize(col, hue), 1.0);
}