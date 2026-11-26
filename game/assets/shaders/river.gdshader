// Flowing river water. UV.x runs across the ribbon, UV.y downstream.
shader_type spatial;
render_mode blend_mix, depth_draw_always, cull_disabled,
    diffuse_burley, specular_schlick_ggx;

uniform vec3 shallow_color : source_color = vec3(0.10, 0.27, 0.28);
uniform vec3 deep_color : source_color = vec3(0.015, 0.07, 0.10);
uniform vec3 foam_color : source_color = vec3(0.76, 0.82, 0.80);
uniform float flow_speed = 0.55;
uniform float normal_strength = 0.35;
uniform float surface_alpha = 0.84;

varying vec2 river_uv;
varying vec3 world_normal;

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float value_noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = hash12(i);
    float b = hash12(i + vec2(1.0, 0.0));
    float c = hash12(i + vec2(0.0, 1.0));
    float d = hash12(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

void vertex() {
    river_uv = UV;
    world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
    // Two crossing ripple fields; the main flow travels along UV.y.
    vec2 p = vec2(river_uv.x * 7.0, river_uv.y * 0.12 - TIME * flow_speed);
    float n1 = value_noise(p);
    float n2 = value_noise(p * 2.3 + vec2(4.7, 1.3));
    vec2 slope = vec2(n1 - n2, n2 - n1) * normal_strength;
    vec3 normal = normalize(world_normal + vec3(slope.x, 0.0, -slope.y));
    NORMAL = normalize((VIEW_MATRIX * vec4(normal, 0.0)).xyz);

    float edge = smoothstep(0.0, 0.18, river_uv.x)
        * (1.0 - smoothstep(0.82, 1.0, river_uv.x));
    float foam = (1.0 - edge) * (0.35 + 0.65 * n1);
    float fresnel = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(-VERTEX)), 0.0, 1.0), 3.0);

    vec3 water = mix(deep_color, shallow_color, 0.25 + 0.65 * n1);
    water = mix(water, foam_color, foam * 0.55);
    water = mix(water, vec3(0.48, 0.62, 0.72), fresnel * 0.35);

    ALBEDO = water;
    ROUGHNESS = mix(0.07, 0.38, foam);
    SPECULAR = 0.68;
    ALPHA = mix(surface_alpha, 0.96, foam);
}
