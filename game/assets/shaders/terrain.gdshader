// Terrain shading with Sentinel-2 orthophoto + landcover masks.
// The ortho is north-up and mapped through a world-space rect; no per-chunk UVs.
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

uniform sampler2D ortho_tex : source_color, filter_linear_mipmap, repeat_disable;
uniform sampler2D landcover_tex : filter_linear_mipmap, repeat_disable;
// x, z origin then width, height (Godot world space).
uniform vec4 map_rect = vec4(-20000.0, -20000.0, 40000.0, 40000.0);
uniform float water_wave_strength = 0.14;

varying vec3 world_pos;
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
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
    vec2 uv = clamp((world_pos.xz - map_rect.xy) / map_rect.zw, vec2(0.0), vec2(1.0));
    vec3 albedo = texture(ortho_tex, uv).rgb;
    vec4 landcover = texture(landcover_tex, uv);

    // Break up the satellite texture at close range without destroying it.
    float macro_detail = value_noise(world_pos.xz * 0.25);
    float fine_detail = value_noise(world_pos.xz * 1.7);
    albedo *= 0.93 + 0.10 * macro_detail + 0.04 * fine_detail;

    // Inland water and sea: cool tint, animated normal, low roughness.
    float water = smoothstep(0.20, 0.75, landcover.r);
    albedo = mix(albedo, albedo * vec3(0.30, 0.55, 0.75), water * 0.75);
    if (water > 0.01) {
        vec2 p = world_pos.xz;
        float h1 = value_noise(p * 0.015 + vec2(TIME * 0.020, 0.0));
        float h2 = value_noise(p * 0.037 - vec2(0.0, TIME * 0.017));
        vec2 grad = vec2(h1 - h2, h2 - h1) * water_wave_strength;
        vec3 water_normal = normalize(world_normal + vec3(grad.x, 0.0, grad.y));
        NORMAL = normalize((VIEW_MATRIX * vec4(water_normal, 0.0)).xyz);
        ROUGHNESS = mix(ROUGHNESS, 0.06, water);
        SPECULAR = mix(SPECULAR, 0.65, water);
    }

    float fresnel = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(-VERTEX)), 0.0, 1.0), 4.0);
    albedo = mix(albedo, vec3(0.48, 0.60, 0.70), fresnel * water * 0.55);

    // Vegetation: gentle green push.
    albedo = mix(albedo, albedo * vec3(0.93, 1.07, 0.88), landcover.g * 0.30);
    // Bare soil / urban: slightly desaturate.
    float grey = dot(albedo, vec3(0.299, 0.587, 0.114));
    albedo = mix(albedo, vec3(grey), landcover.b * 0.22);

    ALBEDO = albedo;
    ROUGHNESS = mix(ROUGHNESS, 0.25, water);
    SPECULAR = mix(SPECULAR, 0.15, water);
}
