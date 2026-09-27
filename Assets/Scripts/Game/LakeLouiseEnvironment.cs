using System;
using UnityEngine;

namespace Game
{
    /// <summary>
    /// Turns AimPreview's placeholder lake into Lake Louise: the Blender-built valley and its
    /// mirrored reflection, turquoise see-through water, and a sky that mirrors itself below
    /// the horizon.
    /// </summary>
    /// <remarks>
    /// Placed in a scene rather than looked up from Resources, so the ordinary Bootstrap scene
    /// is untouched and "Resources stays short". <c>RockSkipping/Environments/Create Lake
    /// Louise Scene</c> makes that scene and fills both fields. AimPreview finds this component
    /// while building its placeholder scene and hands over the water and sky; everything built
    /// here is parented to AimPreview, which Bootstrap marks DontDestroyOnLoad, so it survives
    /// the Lobby/Game/Results loads exactly as the water does.
    ///
    /// All look numbers come from <see cref="look"/> (LakeLouiseLook.json). The Blender preview
    /// in tools/art/lake-louise reads the same file, so the preview and the game cannot drift.
    /// The pipeline and the reasons behind it: docs/plans/lake-louise-overhaul.md.
    /// </remarks>
    public class LakeLouiseEnvironment : MonoBehaviour
    {
        [Tooltip("Assets/Environments/LakeLouise/LakeLouise.fbx")]
        [SerializeField] private GameObject backdropModel;

        [Tooltip("Assets/Environments/LakeLouise/LakeLouiseLook.json")]
        [SerializeField] private TextAsset look;

        /// <summary>Just below the scenery, so the reflection has its own queue.</summary>
        private const int ReflectionQueue = PlaceholderArt.ShoreQueue - 10;

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
            SetHaze(material);
            return material;
        }

        /// <summary>
        /// Builds the valley and its reflection under <paramref name="parent"/> and restyles the
        /// sky. <paramref name="skyMaterial"/> may be null (AimPreview logs why); the scenery is
        /// still built, but the water then shows flat horizon colour where sky should reflect.
        /// </summary>
        public void Build(Transform parent, Material skyMaterial, float waterY)
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

            var scenery = BackdropMaterial(shader, isReflection: false, waterY);
            var reflection = BackdropMaterial(shader, isReflection: true, waterY);

            var real = Place(parent, "LakeLouise", scenery, Vector3.zero, Vector3.one);
            Place(parent, "LakeLouise_Reflection", reflection, new Vector3(0f, 2f * waterY, 0f), new Vector3(1f, -1f, 1f));

            CheckOrientation(real);

            if (skyMaterial != null) StyleSky(skyMaterial);
            else Debug.LogWarning("[LakeLouiseEnvironment] No sky material - the water will reflect " +
                                  "flat horizon colour instead of sky.");
        }

        private GameObject Place(Transform parent, string objectName, Material material, Vector3 position, Vector3 scale)
        {
            var instance = Instantiate(backdropModel);
            instance.name = objectName;
            instance.transform.SetPositionAndRotation(position, Quaternion.identity);
            instance.transform.localScale = scale;
            instance.transform.SetParent(parent, true);

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

        private void SetHaze(Material material)
        {
            material.SetColor("_HazeColour", Colour(_look.haze_colour));
            material.SetFloat("_HazeStart", _look.haze_start);
            material.SetFloat("_HazeDensity", _look.haze_density);
            material.SetFloat("_HazeMax", _look.haze_max);
        }

        /// <summary>
        /// The FBX axis mapping was reasoned, not observed in Unity (see export_fbx in
        /// tools/art/lake-louise/build_lake_louise.py). If it is wrong the valley arrives
        /// rotated or mirrored, and that must say so rather than just look odd.
        /// </summary>
        private static void CheckOrientation(GameObject scenery)
        {
            var forestLeft = scenery.transform.Find("Forest_Left");
            var valley = scenery.transform.Find("Valley");
            if (forestLeft == null || valley == null)
            {
                Debug.LogError("[LakeLouiseEnvironment] LakeLouise.fbx has no Forest_Left/Valley child - " +
                               "was it exported by tools/art/lake-louise/build_lake_louise.py?");
                return;
            }

            var left = forestLeft.GetComponent<Renderer>().bounds.center;
            var lake = valley.GetComponent<Renderer>().bounds.center;
            if (left.x < 0f && lake.z > 0f)
            {
                Debug.Log($"[LakeLouiseEnvironment] Orientation OK: Forest_Left at x={left.x:F0}, valley centre z={lake.z:F0}.");
                return;
            }

            Debug.LogError($"[LakeLouiseEnvironment] LakeLouise.fbx is oriented wrongly: Forest_Left " +
                           $"centre x={left.x:F0} (expected < 0), valley centre z={lake.z:F0} (expected > 0). " +
                           "Fix the axis settings in export_fbx (build_lake_louise.py) and re-export.");
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
