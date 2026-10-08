using System;
using UnityEngine;

namespace Game
{
    // Lives in a scene, not Resources; AimPreview hands it the water and sky, and its look numbers come from
    // LakeLouiseLook.json shared with the Blender preview. doc-ref e918 docs/4-systems/Presentation.md
    public class LakeLouiseEnvironment : MonoBehaviour
    {
        [Tooltip("Assets/Environments/LakeLouise/LakeLouise.fbx")]
        [SerializeField] private GameObject backdropModel;

        [Tooltip("Assets/Environments/LakeLouise/LakeLouiseLook.json")]
        [SerializeField] private TextAsset look;

        /// <summary>Just below the scenery, so the reflection has its own queue.</summary>
        private const int ReflectionQueue = PlaceholderArt.ShoreQueue - 10;

        /// <summary>
        /// Where the FBX was built to be seen from: AimPreview's camera (0, 2, -3), taken at the
        /// waterline. The scenery is scaled about this point, so from the throw position the
        /// picture is unchanged and only its distance grows.
        /// </summary>
        private static readonly Vector2 ModelCameraXZ = new(0f, -3f);

        /// <summary>The far clip the FBX was built inside (AimPreview sets 1000).</summary>
        private const float ModelFarClip = 1000f;

        /// <summary>
        /// Mirrors LakeLouiseLook.json field for field. The snake_case names are the JSON keys:
        /// JsonUtility matches by field name, and the same file is read by Python.
        /// </summary>
        [Serializable]
        private class LookFile
        {
            public float[] sun_direction_towards_sun;
            public string sun_colour;
            public float sun_strength;
            public string ambient_colour;
            public float ambient_strength;
            public string haze_colour;
            public float haze_start;
            public float haze_density;
            public float haze_max;
            public string sky_top;
            public string sky_horizon;
            public float sky_exponent;
            public string cloud_colour;
            public float cloud_amount;
            public float cloud_scale;
            public float cloud_cover;
            public float cloud_dome_offset;
            public float[] cloud_horizon_fade;
            public float cloud_edge;
            public float cloud_detail;
            public string cloud_shadow_colour;
            public float cloud_shade;
            public float[] cloud_shade_offset;
            public string reflection_tint;
            public string water_body;
            public string water_body_far;
            public float water_f0;
            public float water_fresnel_power;
            public float water_body_opacity;
            public float water_streak_strength;
            public float water_streak_fade_distance;
            public float scenery_scale;
        }

        private LookFile _look;

        /// <summary>
        /// The lake's material, or null (logged) if the shader or the look file is missing, in
        /// which case AimPreview keeps the ordinary water.
        /// </summary>
        public Material CreateWaterMaterial()
        {
            if (!TryLoadLook()) return null;
            var shader = LoadShader("LakeLouiseWater");
            if (shader == null) return null;

            var material = new Material(shader) { name = "LakeLouiseWater", renderQueue = PlaceholderArt.WaterQueue };
            material.SetColor("_BodyColour", Colour(_look.water_body));
            material.SetColor("_BodyFarColour", Colour(_look.water_body_far));
            material.SetFloat("_BodyOpacity", _look.water_body_opacity);
            material.SetFloat("_F0", _look.water_f0);
            material.SetFloat("_FresnelPower", _look.water_fresnel_power);
            material.SetFloat("_StreakStrength", _look.water_streak_strength);
            material.SetFloat("_StreakFadeDistance", _look.water_streak_fade_distance);
            SetHaze(material);
            return material;
        }

        // The scenery is scaled about the camera so view angles are unchanged; the water plane and far clip must grow
        // with it or the lake ends in an edge. doc-ref 8efd docs/4-systems/Presentation.md
        public void Build(Transform parent, Material skyMaterial, float waterY, Camera camera, Transform water)
        {
            if (!TryLoadLook()) return;

            if (backdropModel == null)
            {
                Debug.LogError("[LakeLouiseEnvironment] No backdrop model assigned - expected " +
                               "Assets/Environments/LakeLouise/LakeLouise.fbx. Re-run RockSkipping/" +
                               "Environments/Create Lake Louise Scene, or drag it onto this component.");
                return;
            }

            var shader = LoadShader("LakeLouiseBackdrop");
            if (shader == null) return;

            var scale = SceneryScale();
            var anchor = camera != null
                ? new Vector2(camera.transform.position.x, camera.transform.position.z)
                : ModelCameraXZ;

            var real = Place(parent, "LakeLouise", BackdropMaterial(shader, isReflection: false, waterY));
            var turn = CorrectiveTurn(real);
            Arrange(real, turn, scale, anchor, waterY, mirrored: false);

            var reflection = Place(parent, "LakeLouise_Reflection", BackdropMaterial(shader, isReflection: true, waterY));
            Arrange(reflection, turn, scale, anchor, waterY, mirrored: true);

            if (water != null)
            {
                // Uniformly, about its own centre: every metre of lake the scenery gained.
                water.localScale = new Vector3(water.localScale.x * scale, water.localScale.y, water.localScale.z * scale);
            }

            if (camera != null) camera.farClipPlane = Mathf.Max(camera.farClipPlane, ModelFarClip * scale + 200f);

            if (skyMaterial != null) StyleSky(skyMaterial);
            else Debug.LogWarning("[LakeLouiseEnvironment] No sky material - the water will reflect " +
                                  "flat horizon colour instead of sky.");
        }

        private float SceneryScale()
        {
            if (_look.scenery_scale > 0f) return _look.scenery_scale;
            Debug.LogWarning("[LakeLouiseEnvironment] scenery_scale missing or not positive in " +
                             "LakeLouiseLook.json - using 1 (the size the FBX was built at).");
            return 1f;
        }

        private GameObject Place(Transform parent, string objectName, Material material)
        {
            var instance = Instantiate(backdropModel, parent, false);
            instance.name = objectName;

            foreach (var renderer in instance.GetComponentsInChildren<MeshRenderer>())
            {
                var slots = new Material[renderer.sharedMaterials.Length];
                for (var i = 0; i < slots.Length; i++) slots[i] = material;
                renderer.sharedMaterials = slots;

                // A backdrop: casting would drop mountain-sized shadows on the lake the stones
                // are judged against, and it is lit by its own shader anyway.
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return instance;
        }

        /// <summary>
        /// Maps model point p to anchor + turn * (S * (p - modelAnchor)), where S scales by
        /// <paramref name="scale"/> and, for the reflection, mirrors y about the waterline.
        /// Anchors sit at the waterline, so the water level never moves.
        /// </summary>
        private static void Arrange(GameObject instance, Quaternion turn, float scale, Vector2 anchor, float waterY, bool mirrored)
        {
            var scaling = new Vector3(scale, mirrored ? -scale : scale, scale);
            var modelAnchor = new Vector3(ModelCameraXZ.x, waterY, ModelCameraXZ.y);
            var worldAnchor = new Vector3(anchor.x, waterY, anchor.y);

            instance.transform.SetPositionAndRotation(
                worldAnchor - turn * Vector3.Scale(scaling, modelAnchor), turn);
            instance.transform.localScale = scaling;
        }

        private Material BackdropMaterial(Shader shader, bool isReflection, float waterY)
        {
            var material = new Material(shader)
            {
                name = isReflection ? "LakeLouiseBackdrop_Reflection" : "LakeLouiseBackdrop",
                renderQueue = isReflection ? ReflectionQueue : PlaceholderArt.ShoreQueue,
            };

            var sun = _look.sun_direction_towards_sun;
            material.SetVector("_SunDirection", new Vector4(sun[0], sun[1], sun[2], 0f));
            material.SetColor("_SunColour", Colour(_look.sun_colour));
            material.SetFloat("_SunStrength", _look.sun_strength);
            material.SetColor("_AmbientColour", Colour(_look.ambient_colour));
            material.SetFloat("_AmbientStrength", _look.ambient_strength);
            material.SetColor("_ReflectionTint", Colour(_look.reflection_tint));
            material.SetFloat("_Reflection", isReflection ? 1f : 0f);
            // Double-sided for the mirror only: culling it left rows of missing facets that
            // showed as dark lines across the reflection. See _Cull in LakeLouiseBackdrop.shader.
            material.SetFloat("_Cull", (float)(isReflection
                ? UnityEngine.Rendering.CullMode.Off
                : UnityEngine.Rendering.CullMode.Back));
            material.SetFloat("_WaterY", waterY);
            SetHaze(material);
            return material;
        }

        private void StyleSky(Material sky)
        {
            sky.SetColor("_SkyTop", Colour(_look.sky_top));
            sky.SetColor("_SkyHorizon", Colour(_look.sky_horizon));
            sky.SetFloat("_Exponent", _look.sky_exponent);
            sky.SetColor("_CloudColour", Colour(_look.cloud_colour));
            sky.SetFloat("_CloudAmount", _look.cloud_amount);
            sky.SetFloat("_CloudScale", _look.cloud_scale);
            sky.SetFloat("_CloudCover", _look.cloud_cover);
            sky.SetFloat("_CloudDomeOffset", _look.cloud_dome_offset);
            sky.SetFloat("_CloudFadeStart", _look.cloud_horizon_fade[0]);
            sky.SetFloat("_CloudFadeEnd", _look.cloud_horizon_fade[1]);
            sky.SetFloat("_CloudEdge", _look.cloud_edge);
            sky.SetFloat("_CloudDetail", _look.cloud_detail);
            sky.SetColor("_CloudShadowColour", Colour(_look.cloud_shadow_colour));
            sky.SetFloat("_CloudShade", _look.cloud_shade);
            sky.SetVector("_CloudShadeOffset", new Vector4(_look.cloud_shade_offset[0], _look.cloud_shade_offset[1], 0f, 0f));
            sky.SetFloat("_MirrorBelowHorizon", 1f);
            sky.SetColor("_ReflectionTint", Colour(_look.reflection_tint));
        }

        /// <summary>Haze distances stretch with the scenery, so it hazes exactly as built.</summary>
        private void SetHaze(Material material)
        {
            var scale = SceneryScale();
            material.SetColor("_HazeColour", Colour(_look.haze_colour));
            material.SetFloat("_HazeStart", _look.haze_start * scale);
            material.SetFloat("_HazeDensity", _look.haze_density / scale);
            material.SetFloat("_HazeMax", _look.haze_max);
        }

        // The valley's turn is measured on the raw instance; a 180-degree error is corrected with a
        // warning, anything else is an error. doc-ref 2ccd docs/4-systems/Presentation.md
        private static Quaternion CorrectiveTurn(GameObject scenery)
        {
            var forestLeft = scenery.transform.Find("Forest_Left");
            var valley = scenery.transform.Find("Valley");
            if (forestLeft == null || valley == null)
            {
                Debug.LogError("[LakeLouiseEnvironment] LakeLouise.fbx has no Forest_Left/Valley child - " +
                               "was it exported by tools/art/lake-louise/build_lake_louise.py?");
                return Quaternion.identity;
            }

            var left = forestLeft.GetComponent<Renderer>().bounds.center;
            var lake = valley.GetComponent<Renderer>().bounds.center;

            if (left.x < 0f && lake.z > 0f)
            {
                Debug.Log($"[LakeLouiseEnvironment] Orientation OK: Forest_Left at x={left.x:F0}, valley centre z={lake.z:F0}.");
                return Quaternion.identity;
            }

            if (left.x > 0f && lake.z < 0f)
            {
                Debug.LogWarning("[LakeLouiseEnvironment] LakeLouise.fbx imported turned 180 degrees " +
                                 $"(Forest_Left x={left.x:F0}, valley centre z={lake.z:F0}); turning it back. " +
                                 "Re-export with build_lake_louise.py to remove this warning.");
                return Quaternion.Euler(0f, 180f, 0f);
            }

            Debug.LogError($"[LakeLouiseEnvironment] LakeLouise.fbx is oriented wrongly and not by a simple " +
                           $"turn: Forest_Left centre x={left.x:F0} (expected < 0), valley centre z={lake.z:F0} " +
                           "(expected > 0). Check the axis settings in export_fbx (build_lake_louise.py).");
            return Quaternion.identity;
        }

        private bool TryLoadLook()
        {
            if (_look != null) return true;

            if (look == null)
            {
                Debug.LogError("[LakeLouiseEnvironment] No look file assigned - expected " +
                               "Assets/Environments/LakeLouise/LakeLouiseLook.json.");
                return false;
            }

            _look = JsonUtility.FromJson<LookFile>(look.text);
            if (_look?.sun_direction_towards_sun is not { Length: 3 } || _look.cloud_horizon_fade is not { Length: 2 }
                || _look.cloud_shade_offset is not { Length: 2 })
            {
                Debug.LogError("[LakeLouiseEnvironment] LakeLouiseLook.json is missing sun_direction_towards_sun " +
                               "(3 numbers), cloud_horizon_fade (2 numbers) or cloud_shade_offset (2 numbers).");
                _look = null;
                return false;
            }

            return true;
        }

        private static Shader LoadShader(string shaderName)
        {
            var shader = Resources.Load<Shader>(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[LakeLouiseEnvironment] Resources/{shaderName}.shader NOT FOUND.");
                return null;
            }

            if (!shader.isSupported)
            {
                Debug.LogError($"[LakeLouiseEnvironment] Resources/{shaderName}.shader FAILED TO COMPILE - " +
                               "the shader error is logged above this.");
                return null;
            }

            return shader;
        }

        /// <summary>
        /// LakeLouiseLook.json colours are sRGB hex. Material.SetColor treats a Color as sRGB
        /// and converts it for the linear-space project, which is what these are.
        /// </summary>
        private static Color Colour(string hex)
        {
            if (ColorUtility.TryParseHtmlString(hex, out var colour)) return colour;
            Debug.LogError($"[LakeLouiseEnvironment] '{hex}' in LakeLouiseLook.json is not a colour; using magenta.");
            return Color.magenta;
        }
    }
}
