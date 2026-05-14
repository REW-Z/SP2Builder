using System.Runtime.InteropServices;
using UnityEngine;

namespace SP2Builder.ManifoldRuntime
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct PackedManifoldVertex
	{
		public float Px;
		public float Py;
		public float Pz;

		public float Nx;
		public float Ny;
		public float Nz;

		public PackedManifoldVertex(Vector3 position, Vector3 normal)
		{
			Px = position.x;
			Py = position.y;
			Pz = position.z;
			Nx = normal.x;
			Ny = normal.y;
			Nz = normal.z;
		}

		public Vector3 Position => new Vector3(Px, Py, Pz);

		public Vector3 Normal => new Vector3(Nx, Ny, Nz);
	}
}