using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;


internal static class XmlUtil
{
	private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

	private const int Tuple4Count = 4;

	public static XDocument LoadDocument(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("XML path is empty.", nameof(path));
		}

		return XDocument.Parse(File.ReadAllText(path));
	}

	public static void SaveDocument(XDocument document, string path)
	{
		if (document == null)
		{
			throw new ArgumentNullException(nameof(document));
		}

		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("XML path is empty.", nameof(path));
		}

		File.WriteAllText(path, document.Declaration == null ? document.ToString() : document.Declaration + Environment.NewLine + document);
	}

	public static float ParseFloat(string value, float fallback = 0f)
	{
		return float.TryParse(value, NumberStyles.Float, Invariant, out float parsed) ? parsed : fallback;
	}

	public static int ParseInt(string value, int fallback = 0)
	{
		return int.TryParse(value, NumberStyles.Integer, Invariant, out int parsed) ? parsed : fallback;
	}

	public static bool ParseBool(string value, bool fallback = false)
	{
		return bool.TryParse(value, out bool parsed) ? parsed : fallback;
	}

	public static Vector2 ParseVector2(string value, Vector2 fallback)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		string[] parts = value.Split(',');
		if (parts.Length < 2)
		{
			return fallback;
		}

		return new Vector2(ParseFloat(parts[0], fallback.x), ParseFloat(parts[1], fallback.y));
	}

	public static Vector3 ParseVector3(string value, Vector3 fallback)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		string[] parts = value.Split(',');
		if (parts.Length < 3)
		{
			return fallback;
		}

		return new Vector3(ParseFloat(parts[0], fallback.x), ParseFloat(parts[1], fallback.y), ParseFloat(parts[2], fallback.z));
	}

	public static Float4Value ParseFloat4(string value, Float4Value fallback, bool repeatSingleValue = true)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		string[] parts = value.Split(',');
		if (parts.Length == 1 && repeatSingleValue)
		{
			float single = ParseFloat(parts[0], fallback.X);
			return Float4Value.Repeat(single);
		}

		Float4Value parsed = fallback;
		for (int i = 0; i < Mathf.Min(parts.Length, Tuple4Count); i++)
		{
			parsed[i] = ParseFloat(parts[i], fallback[i]);
		}

		return parsed;
	}

	public static Int4Value ParseInt4(string value, Int4Value fallback, bool repeatSingleValue = true)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		string[] parts = value.Split(',');
		if (parts.Length == 1 && repeatSingleValue)
		{
			int single = ParseInt(parts[0], fallback.X);
			return Int4Value.Repeat(single);
		}

		Int4Value parsed = fallback;
		for (int i = 0; i < Mathf.Min(parts.Length, Tuple4Count); i++)
		{
			parsed[i] = ParseInt(parts[i], fallback[i]);
		}

		return parsed;
	}

	public static Bool4Value ParseBool4(string value, Bool4Value fallback, bool repeatSingleValue = true)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		string[] parts = value.Split(',');
		if (parts.Length == 1 && repeatSingleValue)
		{
			bool single = ParseBool(parts[0], fallback.X);
			return Bool4Value.Repeat(single);
		}

		Bool4Value parsed = fallback;
		for (int i = 0; i < Mathf.Min(parts.Length, Tuple4Count); i++)
		{
			parsed[i] = ParseBool(parts[i], fallback[i]);
		}

		return parsed;
	}

	public static string FormatFloat(float value)
	{
		return value.ToString("0.######", Invariant);
	}

	public static string FormatBool(bool value)
	{
		return value ? "true" : "false";
	}

	public static string FormatVector2(Vector2 value)
	{
		return $"{FormatFloat(value.x)},{FormatFloat(value.y)}";
	}

	public static string FormatVector3(Vector3 value)
	{
		return $"{FormatFloat(value.x)},{FormatFloat(value.y)},{FormatFloat(value.z)}";
	}

	public static string FormatFloat4(Float4Value value)
	{
		return $"{FormatFloat(value.X)},{FormatFloat(value.Y)},{FormatFloat(value.Z)},{FormatFloat(value.W)}";
	}

	public static string FormatInt4(Int4Value value)
	{
		return $"{value.X},{value.Y},{value.Z},{value.W}";
	}

	public static string FormatBool4(Bool4Value value)
	{
		return $"{FormatBool(value.X)},{FormatBool(value.Y)},{FormatBool(value.Z)},{FormatBool(value.W)}";
	}

	public static XElement GetOrCreateChild(XElement parent, string name)
	{
		XElement child = parent.Element(name);
		if (child != null)
		{
			return child;
		}

		child = new XElement(name);
		parent.Add(child);
		return child;
	}

	public static void RemoveChildren(XElement parent, string name)
	{
        foreach (XElement child in parent.Elements(name).ToArray())
		{
			child.Remove();
		}
	}
}

internal static class PreviewMaterialFactory
{
	private static Material _solidMaterial;

	private static Material _glassMaterial;

	private static Material _windowMaterial;

	private static Material _bayMaterial;

	private static Material _otherMaterial;

	private static Material _labelMaterial;

	private static readonly Dictionary<string, Material> ThemedMaterials = new Dictionary<string, Material>();

	public static void ClearThemedMaterialCache()
	{
		foreach (Material material in ThemedMaterials.Values)
		{
			if (material == null)
			{
				continue;
			}

			if (Application.isPlaying)
			{
				UnityEngine.Object.Destroy(material);
			}
			else
			{
				UnityEngine.Object.DestroyImmediate(material);
			}
		}

		ThemedMaterials.Clear();
	}

	public static Material GetFuselageMaterial(Part part, bool transparent)
	{
		if (TryGetThemedMaterial(part, transparent, transparent ? 0.35f : 1f, out Material material))
		{
			return material;
		}

		return GetFuselageMaterial(transparent);
	}

	public static Material GetFuselageMaterial(bool transparent)
	{
		return transparent ? (_glassMaterial ??= CreateMaterial(new Color(0.62f, 0.81f, 0.92f, 0.3f), true)) : (_solidMaterial ??= CreateMaterial(new Color(0.79f, 0.82f, 0.87f), false));
	}

	public static Material GetWindowMaterial(Part part)
	{
		if (TryGetThemedMaterial(part, transparent: true, transparentAlpha: 0.4f, out Material material))
		{
			return material;
		}

		return GetWindowMaterial();
	}

	public static Material GetWindowMaterial()
	{
		return _windowMaterial ??= CreateMaterial(new Color(0.37f, 0.68f, 0.87f, 0.4f), true);
	}

	public static Material GetBayMaterial(Part part)
	{
		if (TryGetThemedMaterial(part, transparent: true, transparentAlpha: 0.45f, out Material material))
		{
			return material;
		}

		return GetBayMaterial();
	}

	public static Material GetBayMaterial()
	{
		return _bayMaterial ??= CreateMaterial(new Color(0.77f, 0.47f, 0.2f, 0.65f), true);
	}

	public static Material GetOtherMaterial(Part part)
	{
		if (TryGetThemedMaterial(part, transparent: false, transparentAlpha: 1f, out Material material))
		{
			return material;
		}

		return GetOtherMaterial();
	}

	public static Material GetOtherMaterial()
	{
		return _otherMaterial ??= CreateMaterial(new Color(0.7f, 0.7f, 0.7f, 0.4f), true);
	}

	public static Material GetLabelMaterial()
	{
		return _labelMaterial ??= CreateMaterial(new Color(0.95f, 0.95f, 0.95f), false);
	}

	private static bool TryGetThemedMaterial(Part part, bool transparent, float transparentAlpha, out Material material)
	{
		material = null;
		if (part == null)
		{
			return false;
		}

		Craft craft = part.GetComponentInParent<Craft>();
		if (craft == null || !craft.TryGetThemeMaterial(part.PrimaryMaterialId, out CraftThemeMaterial themeMaterial))
		{
			return false;
		}

		Color color = themeMaterial.Color;
		if (transparent)
		{
			color.a = Mathf.Min(color.a, transparentAlpha);
		}

		bool effectiveTransparent = transparent || color.a < 0.999f;
		string key = $"{craft.GetInstanceID()}:{part.PrimaryMaterialId}:{effectiveTransparent}:{transparentAlpha:0.###}";
		if (!ThemedMaterials.TryGetValue(key, out material) || material == null)
		{
			material = CreateMaterial(color, effectiveTransparent);
			ThemedMaterials[key] = material;
		}
		ApplyColor(material, color);
		ConfigureSurface(material, effectiveTransparent);
		ApplyThemeProperties(material, themeMaterial, color);

		return true;
	}

	private static Material CreateMaterial(Color color, bool transparent)
	{
		Shader shader = FindPreviewShader(transparent);
		Material material = new Material(shader)
		{
			hideFlags = HideFlags.HideAndDontSave,
		};
		ApplyColor(material, color);
		ConfigureSurface(material, transparent);
		return material;
	}

		private static Shader FindPreviewShader(bool transparent)
		{
			if (!transparent)
			{
				return Shader.Find("Universal Render Pipeline/Lit")
					?? Shader.Find("Universal Render Pipeline/Simple Lit")
					?? Shader.Find("Standard")
					?? Shader.Find("Universal Render Pipeline/Unlit")
					?? Shader.Find("Unlit/Color");
			}

			return Shader.Find("Universal Render Pipeline/Lit")
				?? Shader.Find("Universal Render Pipeline/Unlit")
				?? Shader.Find("Universal Render Pipeline/Simple Lit")
				?? Shader.Find("Standard")
				?? Shader.Find("Unlit/Color");
		}

		private static void ApplyColor(Material material, Color color)
		{
			material.color = color;
			if (material.HasProperty("_BaseColor"))
			{
				material.SetColor("_BaseColor", color);
			}
			if (material.HasProperty("_Color"))
			{
				material.SetColor("_Color", color);
			}
		}

		private static void ConfigureSurface(Material material, bool transparent)
		{
			if (!transparent)
			{
				if (material.HasProperty("_Surface"))
				{
					material.SetFloat("_Surface", 0f);
				}
				if (material.HasProperty("_ZWrite"))
				{
					material.SetInt("_ZWrite", 1);
				}
				return;
			}

			material.SetOverrideTag("RenderType", "Transparent");
			if (material.HasProperty("_Mode"))
			{
				material.SetFloat("_Mode", 3f);
			}
			if (material.HasProperty("_Surface"))
			{
				material.SetFloat("_Surface", 1f);
			}
			if (material.HasProperty("_Blend"))
			{
				material.SetFloat("_Blend", 0f);
			}
			if (material.HasProperty("_SrcBlend"))
			{
				material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
			}
			if (material.HasProperty("_DstBlend"))
			{
				material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
			}
			if (material.HasProperty("_ZWrite"))
			{
				material.SetInt("_ZWrite", 0);
			}
			material.DisableKeyword("_ALPHATEST_ON");
			material.EnableKeyword("_ALPHABLEND_ON");
			material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
			material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
			material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
		}

		private static void ApplyThemeProperties(Material material, CraftThemeMaterial themeMaterial, Color baseColor)
		{
			if (material == null)
			{
				return;
			}

			if (material.HasProperty("_Smoothness"))
			{
				material.SetFloat("_Smoothness", Mathf.Clamp01(themeMaterial.Smoothness));
			}
			if (material.HasProperty("_Glossiness"))
			{
				material.SetFloat("_Glossiness", Mathf.Clamp01(themeMaterial.Smoothness));
			}
			if (material.HasProperty("_Metallic"))
			{
				material.SetFloat("_Metallic", Mathf.Clamp01(themeMaterial.Metallic));
			}

			float emissionStrength = Mathf.Max(themeMaterial.Emission, themeMaterial.EmissionDensity);
			if (emissionStrength <= 0f)
			{
				return;
			}

			Color emissionColor = new Color(baseColor.r, baseColor.g, baseColor.b, 1f) * emissionStrength;
			if (material.HasProperty("_EmissionColor"))
			{
				material.SetColor("_EmissionColor", emissionColor);
			}
			material.EnableKeyword("_EMISSION");
		}
	}
