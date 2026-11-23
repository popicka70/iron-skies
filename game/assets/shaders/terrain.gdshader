// First-pass terrain shading. Deliberately simple: procedural world-space noise for
// macro variation, slope rock and lowland sand. No textures yet, and no repeat seams.
// Photoreal splat/ortho layers land in M2.
shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;

uniform vec3 low_color : source_color = vec3(0.14, 0.22, 0.08);
uniform vec3 high_color : source_color = vec3(0.42, 0.41, 0.20);
uniform vec3 rock_color : source_color = vec3(0.36, 0.34, 0.31);
uniform vec3 sand_color : source_color = vec3(0.58, 0.54, 0.40);

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

float fbm(vec2 p) {
    float value = 0.0;
    float amplitude = 0.5;
    for (int octave = 0; octave < 3; octave++) {
        value += amplitude * value_noise(p);
        p *= 2.03;
        amplitude *= 0.5;
    }
    return value / 0.875;
}

void vertex() {
    world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
    float macro = fbm(world_pos.xz * 0.00006);
    float mid = fbm(world_pos.xz * 0.0007 + vec2(17.0, -9.0));
    float detail = value_noise(world_pos.xz * 0.03);

    vec3 color = mix(low_color, high_color, smoothstep(0.38, 0.62, macro));
    color *= 0.86 + 0.22 * mid + 0.08 * detail;

    // Coarse agricultural patchwork (placeholder for real landcover splats).
    vec2 fieldCell = floor(world_pos.xz / 420.0);
    float field = hash12(fieldCell);
    color *= mix(vec3(0.88, 1.02, 0.84), vec3(1.08, 1.02, 0.92), smoothstep(0.2, 0.8, field));

    float slope = 1.0 - clamp(world_normal.y, 0.0, 1.0);
    color = mix(color, rock_color, smoothstep(0.28, 0.62, slope));

    float lowland = 1.0 - smoothstep(3.0, 14.0, world_pos.y);
    color = mix(color, sand_color, lowland * 0.55);

    ALBEDO = color;
    ROUGHNESS = 0.96;
    SPECULAR = 0.12;
}
