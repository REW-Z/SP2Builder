using System;
using System.Runtime.InteropServices;

namespace SP2Builder.ManifoldRuntime
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct NativeDouble3
	{
		public double x;
		public double y;
		public double z;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct NativeBox
	{
		public NativeDouble3 min;
		public NativeDouble3 max;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct MeshGLOptions
	{
		public IntPtr run_indices;
		public UIntPtr run_indices_length;
		public IntPtr run_original_ids;
		public UIntPtr run_original_ids_length;
		public IntPtr merge_from_vert;
		public IntPtr merge_to_vert;
		public UIntPtr merge_verts_length;
		public IntPtr halfedge_tangents;
	}

	internal static class ManifoldNativeMethods
	{
		private const string Library = "manifoldc";

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern UIntPtr manifold_meshgl_size();

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern UIntPtr manifold_manifold_size();

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr manifold_meshgl_w_options(
			IntPtr storage,
			IntPtr vertProperties,
			UIntPtr vertCount,
			UIntPtr numProperties,
			IntPtr triangles,
			UIntPtr triangleCount,
			ref MeshGLOptions options);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr manifold_meshgl_merge(IntPtr storage, IntPtr meshGl);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void manifold_destruct_meshgl(IntPtr meshGl);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr manifold_of_meshgl(IntPtr storage, IntPtr meshGl);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr manifold_boolean(IntPtr storage, IntPtr a, IntPtr b, ManifoldOpType operation);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr manifold_copy(IntPtr storage, IntPtr manifold);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr manifold_transform(
			IntPtr storage,
			IntPtr manifold,
			float x1,
			float y1,
			float z1,
			float x2,
			float y2,
			float z2,
			float x3,
			float y3,
			float z3,
			float x4,
			float y4,
			float z4);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr manifold_get_meshgl_w_normals(IntPtr storage, IntPtr manifold, int normalIdx);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern UIntPtr manifold_meshgl_vert_properties_length(IntPtr meshGl);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void manifold_meshgl_vert_properties(IntPtr destination, IntPtr meshGl);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern UIntPtr manifold_meshgl_tri_length(IntPtr meshGl);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void manifold_meshgl_tri_verts(IntPtr destination, IntPtr meshGl);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern int manifold_is_empty(IntPtr manifold);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern ManifoldError manifold_status(IntPtr manifold);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern double manifold_volume(IntPtr manifold);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr manifold_bounding_box(ref NativeBox destination, IntPtr manifold);

		[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
		internal static extern void manifold_destruct_manifold(IntPtr manifold);
	}
}