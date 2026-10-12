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
uniform float u_audioBass;
uniform float u_bmv4inq7b35_amount; // hint_range(0.0, 1.5, 0.01) = 0.5
uniform float u_bmv4inq7b35_amount_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b35_scale; // hint_range(0.3, 5.0, 0.01) = 1.5
uniform float u_bmv4inq7b35_scale_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b35_speed; // hint_range(0.0, 2.0, 0.01) = 0.4
uniform float u_bmv4inq7b35_speed_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b36_scale; // hint_range(0.3, 6.0, 0.01) = 2.0
uniform float u_bmv4inq7b36_scale_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b36_speed; // hint_range(0.0, 3.0, 0.01) = 1.0
uniform float u_bmv4inq7b36_speed_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b37_scale; // hint_range(1.0, 12.0, 0.1) = 4.0
uniform float u_bmv4inq7b37_scale_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b37_speed; // hint_range(0.0, 2.0, 0.01) = 0.5
uniform float u_bmv4inq7b37_speed_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b37_style; // hint_range(0.0, 1.0, 1.0) = 0.0
uniform float u_bmv4inq7b37_blend; // hint_range(0.0, 5.0, 1.0) = 0.0
uniform float u_bmv4inq7b37_mix; // hint_range(0.0, 1.0, 0.01) = 1.0
uniform float u_bmv4inq7b37_mix_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b38_steps; // hint_range(2.0, 16.0, 1.0) = 5.0
uniform float u_bmv4inq7b38_steps_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b39_spread; // hint_range(0.0, 3.0, 0.01) = 1.0
uniform float u_bmv4inq7b39_spread_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b39_cycle; // hint_range(-2.0, 2.0, 0.01) = 0.0
uniform float u_bmv4inq7b39_cycle_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b39_shade; // hint_range(0.0, 1.0, 0.01) = 0.6
uniform float u_bmv4inq7b39_shade_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
uniform float u_bmv4inq7b39_c1r; // hint_range(0.0, 1.0, 0.001) = 1.0
uniform float u_bmv4inq7b39_c1g; // hint_range(0.0, 1.0, 0.001) = 0.16
uniform float u_bmv4inq7b39_c1b; // hint_range(0.0, 1.0, 0.001) = 0.55
uniform float u_bmv4inq7b39_c2r; // hint_range(0.0, 1.0, 0.001) = 0.2
uniform float u_bmv4inq7b39_c2g; // hint_range(0.0, 1.0, 0.001) = 0.35
uniform float u_bmv4inq7b39_c2b; // hint_range(0.0, 1.0, 0.001) = 1.0
uniform float u_bmv4inq7b39_c3r; // hint_range(0.0, 1.0, 0.001) = 0.0
uniform float u_bmv4inq7b39_c3g; // hint_range(0.0, 1.0, 0.001) = 0.9
uniform float u_bmv4inq7b39_c3b; // hint_range(0.0, 1.0, 0.001) = 0.8
uniform float u_bmv4inq7b3a_amount; // hint_range(0.0, 2.0, 0.01) = 0.6
uniform float u_bmv4inq7b3a_amount_m; // hint_range(-1.0, 1.0, 0.01) = 0.5
float hash21(vec2 p) { p = fract(p * vec2(234.34, 435.345)); p += dot(p, p + 34.23); return fract(p.x * p.y); }
float vnoise(vec2 p) {
  vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
  float a = hash21(i); float b = hash21(i + vec2(1.0, 0.0));
  float c = hash21(i + vec2(0.0, 1.0)); float d = hash21(i + vec2(1.0, 1.0));
  return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}
float fbm(vec2 p) {
  float s = 0.0; float a = 0.5;
  for (int i = 0; i < 5; i++) { s += a * vnoise(p); p = p * 2.03 + vec2(1.7, 9.2); a *= 0.5; }
  return s / 0.96875;
}
float blendV(float a, float b, float mode, float amt) {
  float r = a + b;
  if (mode > 0.5) r = a * b;
  if (mode > 1.5) r = max(a, b);
  if (mode > 2.5) r = min(a, b);
  if (mode > 3.5) r = abs(a - b);
  if (mode > 4.5) r = 1.0 - (1.0 - a) * (1.0 - b);
  return clamp(mix(a, r, amt), 0.0, 1.0);
}
vec3 grad3(float k, vec3 a, vec3 b, vec3 c) {
  k = fract(k) * 3.0;
  vec3 r = mix(a, b, smoothstep(0.0, 1.0, clamp(k, 0.0, 1.0)));
  r = mix(r, c, smoothstep(0.0, 1.0, clamp(k - 1.0, 0.0, 1.0)));
  return mix(r, a, smoothstep(0.0, 1.0, clamp(k - 2.0, 0.0, 1.0)));
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
  {
    vec2 q = p * (clamp(u_bmv4inq7b35_scale + u_bmv4inq7b35_scale_m * 4.7 * u_audioBass, 0.3, 5.0)); float tt = t * (clamp(u_bmv4inq7b35_speed + u_bmv4inq7b35_speed_m * 2.0 * u_audioBass, 0.0, 2.0)) * 0.3;
    vec2 w = vec2(fbm(q + vec2(tt, 0.0)), fbm(q + vec2(5.2, 1.3) - vec2(0.0, tt)));
    p += (w - 0.5) * 2.0 * (clamp(u_bmv4inq7b35_amount + u_bmv4inq7b35_amount_m * 1.5 * u_audioBass, 0.0, 1.5));
  }
  { float f = 0.0;
    vec2 q = p * (clamp(u_bmv4inq7b36_scale + u_bmv4inq7b36_scale_m * 5.7 * u_audioBass, 0.3, 6.0)); float tt = t * (clamp(u_bmv4inq7b36_speed + u_bmv4inq7b36_speed_m * 3.0 * u_audioBass, 0.0, 3.0));
    float s = sin(q.x + tt) + sin(q.y * 1.3 - tt * 0.8) + sin((q.x + q.y) * 0.7 + tt * 1.2)
      + sin(length(q + 2.0 * vec2(sin(tt * 0.3), cos(tt * 0.4))) * 1.5 - tt);
    f = 0.5 + 0.125 * s;
    v = f; }
  { float f = 0.0;
    vec2 q = p * (clamp(u_bmv4inq7b37_scale + u_bmv4inq7b37_scale_m * 11.0 * u_audioBass, 1.0, 12.0)) * 0.5; vec2 id = floor(q); vec2 fq = fract(q); float d1 = 8.0; float d2 = 8.0;
    for (int j = -1; j <= 1; j++) {
      for (int i = -1; i <= 1; i++) {
        vec2 g = vec2(float(i), float(j));
        vec2 h = vec2(hash21(id + g), hash21(id + g + 17.3));
        float d = length(g + 0.5 + 0.4 * sin(t * (clamp(u_bmv4inq7b37_speed + u_bmv4inq7b37_speed_m * 2.0 * u_audioBass, 0.0, 2.0)) + 6.2831853 * h) - fq);
        if (d < d1) { d2 = d1; d1 = d; } else if (d < d2) { d2 = d; }
      }
    }
    f = (u_bmv4inq7b37_style) < 0.5 ? clamp(d1, 0.0, 1.0) : clamp((d2 - d1) * 2.0, 0.0, 1.0);
    v = blendV(v, f, u_bmv4inq7b37_blend, clamp(u_bmv4inq7b37_mix + u_bmv4inq7b37_mix_m * 1.0 * u_audioBass, 0.0, 1.0)); }
  {
    v = min(floor(v * (floor(clamp(u_bmv4inq7b38_steps + u_bmv4inq7b38_steps_m * 14.0 * u_audioBass, 2.0, 16.0) + 0.5))), (floor(clamp(u_bmv4inq7b38_steps + u_bmv4inq7b38_steps_m * 14.0 * u_audioBass, 2.0, 16.0) + 0.5)) - 1.0) / ((floor(clamp(u_bmv4inq7b38_steps + u_bmv4inq7b38_steps_m * 14.0 * u_audioBass, 2.0, 16.0) + 0.5)) - 1.0);
  }
  {
    col = grad3(v * (clamp(u_bmv4inq7b39_spread + u_bmv4inq7b39_spread_m * 3.0 * u_audioBass, 0.0, 3.0)) + hue + t * (clamp(u_bmv4inq7b39_cycle + u_bmv4inq7b39_cycle_m * 4.0 * u_audioBass, -2.0, 2.0)) * 0.1, (vec3(u_bmv4inq7b39_c1r, u_bmv4inq7b39_c1g, u_bmv4inq7b39_c1b)), (vec3(u_bmv4inq7b39_c2r, u_bmv4inq7b39_c2g, u_bmv4inq7b39_c2b)), (vec3(u_bmv4inq7b39_c3r, u_bmv4inq7b39_c3g, u_bmv4inq7b39_c3b))) * mix(1.0, v, (clamp(u_bmv4inq7b39_shade + u_bmv4inq7b39_shade_m * 1.0 * u_audioBass, 0.0, 1.0)));
  }
  {
    col *= 1.0 + (clamp(u_bmv4inq7b3a_amount + u_bmv4inq7b3a_amount_m * 2.0 * u_audioBass, 0.0, 2.0)) * v * v;
  }
  fragColor = vec4(finalize(col, hue), 1.0);
}