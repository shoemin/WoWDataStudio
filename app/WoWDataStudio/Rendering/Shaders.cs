namespace WoWDataStudio.Rendering;

/// <summary>
/// GLSL 3.30 shaders. The model shader reproduces the 1.12 fixed-function setup of M2Scene_SetBatchTextureStages /
/// SetBatchLightingAndFog / SetBatchBlendState (decompile @0070b740 / @0070baf0 / @0070c190):
///  * diffuse colour = tint x colour track, alpha = colour alpha x transparency weight; unlit batches and the mod /
///    mod2x blend modes (g_M2BlendFogEnableTable {1,1,1,1,1,0,0}) are not lit; mod uses an emissive 1.0, mod2x 0.5;
///  * texture combiner per blend mode (g_M2BlendTextureStageTable {0,1,1,1,1,5,5}): MODULATE for modes 0-4,
///    BLENDCURRENTALPHA (texture x texture alpha + current x (1 - texture alpha)) for mod / mod2x;
///  * a layer whose tex-unit lookup is >= 3 gets sphere environment coordinates, otherwise its texture transform;
///  * alpha key: discard below alpha x 224/255; blended modes: discard below 1/255.
/// Lighting is the viewer's own simple, readable default: a light from the upper left of the camera plus ambient.
/// </summary>
internal static class Shaders
{
    public const string ModelVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aUv;
        layout(location = 3) in vec2 aUv2;
        layout(location = 4) in uvec4 aBones;
        layout(location = 5) in vec4 aWeights;
        uniform mat4 uViewProj;
        uniform mat4 uView;
        uniform sampler2D uBoneTex;
        uniform int uSkinned;
        uniform mat4 uTexMat0;
        uniform mat4 uTexMat1;
        uniform int uEnv0;
        uniform int uEnv1;
        out vec3 vNormal;
        out vec2 vUv0;
        out vec2 vUv1;
        mat4 boneMat(uint i) {
            int r = int(i);
            return mat4(texelFetch(uBoneTex, ivec2(0, r), 0), texelFetch(uBoneTex, ivec2(1, r), 0),
                        texelFetch(uBoneTex, ivec2(2, r), 0), texelFetch(uBoneTex, ivec2(3, r), 0));
        }
        void main() {
            vec4 p = vec4(aPos, 1.0);
            vec3 n = aNormal;
            if (uSkinned == 1) {
                mat4 m = boneMat(aBones.x) * aWeights.x + boneMat(aBones.y) * aWeights.y
                       + boneMat(aBones.z) * aWeights.z + boneMat(aBones.w) * aWeights.w;
                float wsum = aWeights.x + aWeights.y + aWeights.z + aWeights.w;
                if (wsum > 0.0) {
                    p = m * p;
                    p /= wsum;
                    p.w = 1.0;
                    n = mat3(m) * n;
                }
            }
            gl_Position = uViewProj * p;
            vec3 nv = normalize(mat3(uView) * n);
            vNormal = nv;
            vec2 env = vec2(0.5 + 0.5 * nv.x, 0.5 - 0.5 * nv.y);
            vUv0 = uEnv0 == 1 ? env : (uTexMat0 * vec4(aUv, 0.0, 1.0)).xy;
            vUv1 = uEnv1 == 1 ? env : (uTexMat1 * vec4(aUv2, 0.0, 1.0)).xy;
        }
        """;

    public const string ModelFs = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vUv0;
        in vec2 vUv1;
        uniform sampler2D uTex0;
        uniform sampler2D uTex1;
        uniform int uLayers;
        uniform int uCombiner;      // 0 modulate, 1 blend by texture alpha (mod / mod2x)
        uniform vec4 uColor;        // diffuse rgb, batch alpha
        uniform vec3 uEmissive;
        uniform int uLit;
        uniform vec3 uLightDir;     // view space, towards the light
        uniform vec3 uAmbient;
        uniform vec3 uDiffuse;
        uniform float uAlphaRef;    // < 0: no alpha test
        uniform int uSolid;         // 1: flat colour (wireframe overlay)
        uniform vec4 uSolidColor;
        out vec4 frag;
        void main() {
            if (uSolid == 1) { frag = uSolidColor; return; }
            vec3 light = vec3(1.0);
            if (uLit == 1) {
                vec3 n = normalize(vNormal);
                if (!gl_FrontFacing) n = -n;
                light = uAmbient + uDiffuse * max(dot(n, uLightDir), 0.0);
            }
            vec4 cur = vec4(clamp(uColor.rgb * light + uEmissive, 0.0, 1.0), uColor.a);
            vec4 t0 = texture(uTex0, vUv0);
            vec4 c;
            if (uCombiner == 1) c = vec4(mix(cur.rgb, t0.rgb, t0.a), cur.a * t0.a);
            else c = cur * t0;
            if (uLayers > 1) {
                vec4 t1 = texture(uTex1, vUv1);
                if (uCombiner == 1) c = vec4(mix(c.rgb, t1.rgb, t1.a), c.a * t1.a);
                else c *= t1;
            }
            if (uAlphaRef >= 0.0 && c.a < uAlphaRef) discard;
            frag = c;
        }
        """;

    /// <summary>M3: the particles and ribbons of another version's models (position, texture coordinates, colour).</summary>
    public const string EffectVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColor;
        uniform mat4 uViewProj;
        out vec2 vUv;
        out vec4 vColor;
        void main() {
            gl_Position = uViewProj * vec4(aPos, 1.0);
            vUv = aUv;
            vColor = aColor;
        }
        """;

    public const string EffectFs = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColor;
        uniform sampler2D uTex;
        uniform float uAlphaRef;    // < 0: no alpha test
        out vec4 frag;
        void main() {
            vec4 c = texture(uTex, vUv) * vColor;
            if (uAlphaRef >= 0.0 && c.a < uAlphaRef) discard;
            frag = c;
        }
        """;

    public const string LineVs = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec4 aColor;
        uniform mat4 uViewProj;
        out vec4 vColor;
        void main() {
            gl_Position = uViewProj * vec4(aPos, 1.0);
            vColor = aColor;
        }
        """;

    public const string LineFs = """
        #version 330 core
        in vec4 vColor;
        out vec4 frag;
        void main() { frag = vColor; }
        """;
}
