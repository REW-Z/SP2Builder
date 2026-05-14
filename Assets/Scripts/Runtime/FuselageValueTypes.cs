using System;
using UnityEngine;


[Serializable]
public struct Float4Value
{
	public float X;

	public float Y;

	public float Z;

	public float W;

	public Float4Value(float x, float y, float z, float w)
	{
		X = x;
		Y = y;
		Z = z;
		W = w;
	}

	public float this[int index]
	{
		get
		{
			return index switch
			{
				0 => X,
				1 => Y,
				2 => Z,
				3 => W,
				_ => throw new IndexOutOfRangeException()
			};
		}
		set
		{
			switch (index)
			{
				case 0:
					X = value;
					break;
				case 1:
					Y = value;
					break;
				case 2:
					Z = value;
					break;
				case 3:
					W = value;
					break;
				default:
					throw new IndexOutOfRangeException();
			}
		}
	}

	public static Float4Value Repeat(float value)
	{
		return new Float4Value(value, value, value, value);
	}

	public static Float4Value Lerp(Float4Value a, Float4Value b, float t)
	{
		return new Float4Value(
			Mathf.Lerp(a.X, b.X, t),
			Mathf.Lerp(a.Y, b.Y, t),
			Mathf.Lerp(a.Z, b.Z, t),
			Mathf.Lerp(a.W, b.W, t));
	}

	public static Float4Value Max(Float4Value a, Float4Value b)
	{
		return new Float4Value(
			Mathf.Max(a.X, b.X),
			Mathf.Max(a.Y, b.Y),
			Mathf.Max(a.Z, b.Z),
			Mathf.Max(a.W, b.W));
	}

	// 返回四个分量中的最大值。 / Return the largest component among the four stored values.
	public float MaxComponent()
	{
		return Mathf.Max(Mathf.Max(X, Y), Mathf.Max(Z, W));
	}

	public override readonly string ToString()
	{
		return $"{X},{Y},{Z},{W}";
	}
}

[Serializable]
public struct Int4Value
{
	public int X;

	public int Y;

	public int Z;

	public int W;

	public Int4Value(int x, int y, int z, int w)
	{
		X = x;
		Y = y;
		Z = z;
		W = w;
	}

	public int this[int index]
	{
		get
		{
			return index switch
			{
				0 => X,
				1 => Y,
				2 => Z,
				3 => W,
				_ => throw new IndexOutOfRangeException()
			};
		}
		set
		{
			switch (index)
			{
				case 0:
					X = value;
					break;
				case 1:
					Y = value;
					break;
				case 2:
					Z = value;
					break;
				case 3:
					W = value;
					break;
				default:
					throw new IndexOutOfRangeException();
			}
		}
	}

	public static Int4Value Repeat(int value)
	{
		return new Int4Value(value, value, value, value);
	}

	public static Int4Value Max(Int4Value a, Int4Value b)
	{
		return new Int4Value(
			Mathf.Max(a.X, b.X),
			Mathf.Max(a.Y, b.Y),
			Mathf.Max(a.Z, b.Z),
			Mathf.Max(a.W, b.W));
	}

	// 返回四个整型分量中的最大值。 / Return the largest component among the four stored integers.
	public int MaxComponent()
	{
		return Mathf.Max(Mathf.Max(X, Y), Mathf.Max(Z, W));
	}

	public override readonly string ToString()
	{
		return $"{X},{Y},{Z},{W}";
	}
}

[Serializable]
public struct Bool4Value
{
	public bool X;

	public bool Y;

	public bool Z;

	public bool W;

	public Bool4Value(bool x, bool y, bool z, bool w)
	{
		X = x;
		Y = y;
		Z = z;
		W = w;
	}

	public bool this[int index]
	{
		get
		{
			return index switch
			{
				0 => X,
				1 => Y,
				2 => Z,
				3 => W,
				_ => throw new IndexOutOfRangeException()
			};
		}
		set
		{
			switch (index)
			{
				case 0:
					X = value;
					break;
				case 1:
					Y = value;
					break;
				case 2:
					Z = value;
					break;
				case 3:
					W = value;
					break;
				default:
					throw new IndexOutOfRangeException();
			}
		}
	}

	public static Bool4Value Repeat(bool value)
	{
		return new Bool4Value(value, value, value, value);
	}

	// 把四个布尔值转换成 0/1 的 Float4 掩码。 / Convert the four booleans into a 0/1 Float4 mask.
	public Float4Value ToFloatMask()
	{
		return new Float4Value(X ? 1f : 0f, Y ? 1f : 0f, Z ? 1f : 0f, W ? 1f : 0f);
	}

	public static Bool4Value FromFloatMask(Float4Value value)
	{
		return new Bool4Value(value.X > 0.5f, value.Y > 0.5f, value.Z > 0.5f, value.W > 0.5f);
	}

	public override readonly string ToString()
	{
		return $"{X},{Y},{Z},{W}";
	}
}