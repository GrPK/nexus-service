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
uniform float u_bmv4inq7f4d_amount; // hint_range(0.0, 1.5, 0.01) = 0.6
uniform float u_bmv4inq7f4d_scale; // hint_range(0.3, 5.0, 0.01) = 1.2
uniform float u_bmv4inq7f4d_speed; // hint_range(0.0, 2.0, 0.01) = 0.3
uniform float u_bmv4inq7f4e_scale; // hint_range(0.3, 8.0, 0.01) = 1.5
uniform float u_bmv4inq7f4e_speed; // hint_range(0.0, 2.0, 0.01) = 0.2
uniform float u_bmv4inq7f4f_level; // hint_range(0.0, 1.0, 0.01) = 0.58
uniform float u_bmv4inq7f4f_softness; // hint_range(0.005, 0.5, 0.005) = 0.12
uniform float u_bmv4inq7f4g_palette; // hint_range(0.0, 8.0, 1.0) = 4.0
uniform float u_bmv4inq7f4g_spread; // hint_range(0.0, 3.0, 0.01) = 0.9
uniform float u_bmv4inq7f4g_offset; // hint_range(0.0, 1.0, 0.01) = 0.0
uniform float u_bmv4inq7f4g_cycle; // hint_range(-2.0, 2.0, 0.01) = 0.0
uniform float u_bmv4inq7f4g_shade; // hint_range(0.0, 1.0, 0.01) = 0.85
uniform float u_bmv4inq7f4h_amount; // hint_range(0.0, 2.0, 0.01) = 0.4
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
  {
    vec2 q = p * (u_bmv4inq7f4d_scale); float tt = t * (u_bmv4inq7f4d_speed) * 0.3;
    vec2 w = vec2(fbm(q + vec2(tt, 0.0)), fbm(q + vec2(5.2, 1.3) - vec2(0.0, tt)));
    p += (w - 0.5) * 2.0 * (u_bmv4inq7f4d_amount);
  }
  { float f = 0.0;
    f = clamp((fbm(p * (u_bmv4inq7f4e_scale) + vec2(t * (u_bmv4inq7f4e_speed) * 0.2, -t * (u_bmv4inq7f4e_speed) * 0.15)) - 0.5) * 1.8 + 0.5, 0.0, 1.0);
    v = f; }
  {
    v = smoothstep((u_bmv4inq7f4f_level) - (u_bmv4inq7f4f_softness), (u_bmv4inq7f4f_level) + (u_bmv4inq7f4f_softness), v);
  }
  {
    col = presetPal(v * (u_bmv4inq7f4g_spread) + (u_bmv4inq7f4g_offset) + hue + t * (u_bmv4inq7f4g_cycle) * 0.1, (u_bmv4inq7f4g_palette)) * mix(1.0, v, (u_bmv4inq7f4g_shade));
  }
  {
    col *= 1.0 + (u_bmv4inq7f4h_amount) * v * v;
  }
  fragColor = vec4(finalize(col, hue), 1.0);
}