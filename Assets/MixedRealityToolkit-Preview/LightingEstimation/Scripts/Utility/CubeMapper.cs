// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;
using UnityEngine;

namespace Microsoft.MixedReality.Toolkit.LightEstimation
{
	public class CubeMapper
	{
		#region Support data
		struct StampDir
		{
			public Quaternion direction;
			public Vector3    position;
			public float      timestamp;
		}

		const int   cSphereRows    = 12;
		const int   cSphereColumns = 18;
		const float cSphereSize    = 10;
		const int   cGeometryLayer = 30;
		#endregion

		#region Fields
		GameObject root;
		Camera     cam;
		Cubemap    map;
		Renderer   imageQuad;
		Renderer   imageSphere;
		Material   skybox;
		float      fov;
		bool       hdr;
	
		bool           needsFullStamp      = true;
		float          stampExpireDistance = 0;
		float          stampExpireLife     = 0;
		List<StampDir> stampCache          = new List<StampDir>();
		#endregion

		#region Properties
		public Cubemap  Map                 { get { return map; } }
		public Material SkyMaterial         { get { return skybox; } }
		public float    StampExpireDistance { get { return stampExpireDistance; } set { stampExpireDistance = value; } }
		#endregion

		#region Public Methods
		public void Create(float stampFOVDeg, int resolution=128, bool HDR=false)
		{
			fov = stampFOVDeg;
			hdr = HDR;
		
			map = new Cubemap(resolution, hdr ? TextureFormat.RGBAHalf : TextureFormat.RGB24, true);

			skybox = new Material(Shader.Find("Skybox/Cubemap"));
			skybox.SetTexture("_Tex", map);

			// Create the root object
			root = new GameObject("_Cubemap Generator Root");
			root.SetActive(false);
			// This throws errors when destroying objects at runtime
			//_root.hideFlags = HideFlags.DontSave;

			// Create a camera to render with
			GameObject camObj = new GameObject("RenderCamera");
			cam = camObj.AddComponent<Camera>();
			cam.cullingMask     = 1 << cGeometryLayer;
			cam.clearFlags      = CameraClearFlags.Skybox;
			cam.backgroundColor = new Color(0,0,0,1);
			camObj.transform.SetParent(root.transform);

			// Add a quad for stamping camera pictures with
			GameObject quad = new GameObject("StampQuad");
			imageQuad = quad.AddComponent<MeshRenderer>();
			imageQuad.sharedMaterial           = new Material(Shader.Find("Hidden/LightEstimationStamp"));
			imageQuad.sharedMaterial.hideFlags = HideFlags.DontSave;
			quad.AddComponent<MeshFilter>().sharedMesh = CreatePlane(1f/Mathf.Tan(fov*Mathf.Deg2Rad*0.5f));
			quad.layer = cGeometryLayer;
			quad.transform.SetParent(root.transform);

			// Add a nautilus sphere for initial totally surrounding stamps
			GameObject sphere = new GameObject("StampSphere");
			imageSphere = sphere.AddComponent<MeshRenderer>();
			imageSphere.sharedMaterial = Object.Instantiate(imageQuad.sharedMaterial);
			imageSphere.sharedMaterial.renderQueue-=1;
			imageSphere.hideFlags      = HideFlags.DontSave;
			sphere.AddComponent<MeshFilter>().sharedMesh = CreateNautilusSphere();
			sphere.layer = cGeometryLayer;
			sphere.transform.SetParent(root.transform);
		}
		public void Destroy()
		{
			if (root == null)
				return;
		
			if (Application.isPlaying)
				Object.Destroy(root);
			else
				Object.DestroyImmediate(root);
		}
		public void Stamp(Texture aTex, Vector3 aPosition, Quaternion aOrientation)
		{
			if (imageQuad == null)
			{
				Debug.LogError("[LightEstimation] Please call CubeMapper.Create before attempting to Stamp anything!");
				return;
			}
		
			// Prep objects for rendering
			if (needsFullStamp)
			{
				imageQuad.enabled = true;
				imageSphere.enabled = true;
				imageSphere.transform.rotation = aOrientation;
				imageSphere.sharedMaterial.mainTexture = aTex;
			}
			else
			{
				imageQuad.enabled = true;
				imageSphere.enabled = false;
			}
			cam.transform.rotation = aOrientation;
			imageQuad.transform.rotation = aOrientation;
			imageQuad.sharedMaterial.mainTexture = aTex;
			if (aTex != null)
				imageQuad.transform.localScale = new Vector3(1, (aTex.height / (float)aTex.width), 1) ;
		
			// Figure out which faces of the Cubemap we need to render with this stamp, we don't generally need to 
			// render all of them, (usually 3 at most) so it's a no-brainer optimization, even if it's not 100% accurate
			int     render = 0;
			Vector3 fwd    = cam.transform.forward;
			if (needsFullStamp)
			{
				render = 63;
			}
			else
			{
				if (AngleVisible(fwd,  Vector3.right  )) render |= 1 << (int)CubemapFace.PositiveX;
				if (AngleVisible(fwd, -Vector3.right  )) render |= 1 << (int)CubemapFace.NegativeX;
				if (AngleVisible(fwd,  Vector3.up     )) render |= 1 << (int)CubemapFace.PositiveY;
				if (AngleVisible(fwd, -Vector3.up     )) render |= 1 << (int)CubemapFace.NegativeY;
				if (AngleVisible(fwd,  Vector3.forward)) render |= 1 << (int)CubemapFace.PositiveZ;
				if (AngleVisible(fwd, -Vector3.forward)) render |= 1 << (int)CubemapFace.NegativeZ;
			}

			// render the objects
			root.SetActive(true);
			Material old = RenderSettings.skybox;
			RenderSettings.skybox = skybox;
			cam.RenderToCubemap(map, render);
			RenderSettings.skybox = old;
			root.SetActive(false);
		
			CacheStamp(aPosition, aOrientation);
			needsFullStamp = false;
		}
		public void Clear()
		{
			needsFullStamp = true;
			ClearCache();
		}
		public void ClearCache()
		{
			stampCache.Clear();
		}
		public bool IsCached(Vector3 position, Quaternion orientation)
		{
			float min = float.MaxValue;

			for (int i = 0; i < stampCache.Count; i++)
			{
				StampDir s = stampCache[i];
				if  (IsExpired(i, position))
				{
					stampCache.RemoveAt(i);
					continue;
				}
				float ang = Quaternion.Angle(orientation, s.direction);
				if (min > ang)
					min = ang;
			}

			// allow for a little overlap (using .4 fov instead of .5), especially since fov is horiontal, not vertical, and vertical is 
			// shorter due to aspect ratio
			return min < fov*(9f/16f);
		}
		#endregion

		#region Private Methods
		private Mesh CreatePlane(float aZ, float aBlend = 0.2f)
		{
			Mesh m = new Mesh();
			m.vertices = new Vector3[] { 
				new Vector3(-1,1,aZ), new Vector3(1,1,aZ), new Vector3(1,-1,aZ), new Vector3(-1,-1,aZ),
				new Vector3(-1 + aBlend, 1-aBlend*2, aZ), new Vector3(1-aBlend, 1-aBlend*2, aZ), new Vector3(1-aBlend, -1+aBlend*2, aZ), new Vector3(-1+aBlend, -1+aBlend*2, aZ) };
			m.colors = new Color[] { new Color(1,1,1,0), new Color(1,1,1,0), new Color(1,1,1,0), new Color(1,1,1,0),
				Color.white, Color.white, Color.white, Color.white };
			m.uv = new Vector2[] { new Vector2(0, 1), new Vector2(1,1), new Vector2(1,0), new Vector2(0,0),
				new Vector2(aBlend/2, 1-aBlend), new Vector2(1-aBlend/2, 1-aBlend), new Vector2(1-aBlend/2, aBlend), new Vector2(aBlend/2, aBlend)};
			m.triangles = new int[] {
				0,1,5,  0,5,4,
				5,1,2,  5,2,6,
				6,2,7,  7,2,3,
				7,3,0,  7,0,4,
				4,5,6,  4,6,7};
			m.RecalculateBounds();

			return m;
		}
		private Mesh CreateNautilusSphere()
		{
			float overlap = (360 / cSphereColumns);
			float start   = -180 - overlap/2;

			Vector3[] verts  = new Vector3[cSphereRows*cSphereColumns];
			Vector2[] uvs    = new Vector2[verts.Length];
			Color  [] colors = new Color  [verts.Length];
			int    [] tris   = new int    [verts.Length * 6];

			// Generate vertices for the sphere
			for (int y = 0; y < cSphereRows; y++)
			{
				float yPercent = y / (float)(cSphereRows-1);
				float yAng     = (-Mathf.PI/2f) + yPercent * Mathf.PI;
				for (int x = 0; x < cSphereColumns; x++)
				{
					float xPercent = x / (float)(cSphereColumns-1);
					float xAng     = (start + xPercent * (360f + overlap*1.1f)) * Mathf.Deg2Rad;
				
					Vector3 pt = new Vector3();
					pt.x = Mathf.Cos(xAng) * Mathf.Cos(yAng);
					pt.z = Mathf.Sin(xAng) * Mathf.Cos(yAng);
					pt.y = Mathf.Sin(yAng);

					int i = x+y*cSphereColumns;
					verts [i] = pt*cSphereSize*(1-xPercent*0.1f); // Expand the sphere as it turns to give it a nautilus shape
					colors[i] = new Color(1,1,1, x==cSphereColumns-1?0:1); // last column gets an alpha of 0, so we get a smooth transition
					uvs   [i] = new Vector2(xPercent, yPercent);
				}
			}

			// now index the verts
			for (int y = 0; y < cSphereRows-1; y++)
			{
				for (int x = 0; x < cSphereColumns-1; x++)
				{
					int i = (x + y * cSphereColumns) * 6;
					tris[i]   = x + y * cSphereColumns;
					tris[i+1] = (x+1) + y * cSphereColumns;
					tris[i+2] = (x+1) + (y+1) * cSphereColumns;

					tris[i+3] = x + y * cSphereColumns;
					tris[i+4] = (x+1) + (y+1) * cSphereColumns;
					tris[i+5] = x + (y+1) * cSphereColumns;
				}
			}

			// Build the mesh from the data we've created!
			Mesh m = new Mesh();
			m.vertices  = verts;
			m.colors    = colors;
			m.uv        = uvs;
			m.triangles = tris;
			m.RecalculateBounds();
			return m;
		}
		private bool AngleVisible(Vector3 dir, Vector3 camDir)
		{
			float angle = Vector3.Angle(dir, camDir);
			return angle < 45 + (fov/2);
		}
		private void CacheStamp(Vector3 position, Quaternion orientation)
		{
			StampDir stamp = new StampDir();
			stamp.position  = position;
			stamp.direction = orientation;
			stamp.timestamp = Time.time;
			stampCache.Add(stamp);
		}
		private bool IsExpired(int i, Vector3 cameraPosition)
		{
			if (stampExpireLife     > 0 && Time.time - stampCache[i].timestamp > stampExpireLife)
				return true;
			if (stampExpireDistance > 0 && Vector3.SqrMagnitude(stampCache[i].position - cameraPosition) > stampExpireDistance*stampExpireDistance)
				return true;
			return false;
		}
		#endregion

		#region Static Methods
		public Vector3 GetWeightedDirection(ref Histogram histogram)
		{
			int mipLevel = (int)(Mathf.Log( map.width ) / Mathf.Log(2)) - 2;
			return GetWeightedDirection(map, mipLevel, ref histogram).normalized;
		}
		public static Vector3 GetWeightedDirection(Cubemap map, int mipLevel, ref Histogram histogram)
		{
			if (histogram != null)
				histogram.Clear();

			Vector3 result = Vector3.zero;
			result += Quaternion.Euler(0,-90,0) * GetWeightedDirection(map, CubemapFace.NegativeX, mipLevel, ref histogram);
			result += Quaternion.Euler(90,0,0)  * GetWeightedDirection(map, CubemapFace.NegativeY, mipLevel, ref histogram);
			result += Quaternion.Euler(0,180,0) * GetWeightedDirection(map, CubemapFace.NegativeZ, mipLevel, ref histogram);
			result += Quaternion.Euler(0,90,0)  * GetWeightedDirection(map, CubemapFace.PositiveX, mipLevel, ref histogram);
			result += Quaternion.Euler(-90,0,0) * GetWeightedDirection(map, CubemapFace.PositiveY, mipLevel, ref histogram);
			result += GetWeightedDirection(map, CubemapFace.PositiveZ, mipLevel, ref histogram);
			return result/6f;
		}
		static Vector3 GetWeightedDirection(Cubemap map, CubemapFace face, int mipLevel, ref Histogram histogram)
		{
			Color[] colors    = map.GetPixels( face, mipLevel );
			int     width     = Mathf.Max( map.width >> mipLevel );
			float   texelSize = 2f/width;
			float   start     = -1f + texelSize/2f;
			float   scale     = 1f/(width*width);

			Vector3 result = Vector3.zero;
			for (int y = 0; y < width; y++)
			{
				float yP = -(start + texelSize * y);
				for (int x = 0; x < width; x++)
				{
					float xP        = start + texelSize * x;
					Color color = colors[x+y*width];
					float intensity = color.grayscale;
					if (histogram != null)
						histogram.Add(color.r, color.g, color.b, intensity);
				
					result += new Vector3(xP, yP, 1).normalized * intensity;
				}
			}
			return result * scale;
		}

		public static Texture2D CreateCubemapTex(Cubemap map)
		{
			Texture2D tex  = new Texture2D(map.width * 6, map.height, TextureFormat.RGB24, false);
			Color[]   data = new Color[tex.width * tex.height];

			CopyFace(ref data, map, CubemapFace.PositiveX, 0);
			CopyFace(ref data, map, CubemapFace.NegativeX, 1*map.width);
			CopyFace(ref data, map, CubemapFace.PositiveY, 2*map.width);
			CopyFace(ref data, map, CubemapFace.NegativeY, 3*map.width);
			CopyFace(ref data, map, CubemapFace.PositiveZ, 4*map.width);
			CopyFace(ref data, map, CubemapFace.NegativeZ, 5*map.width);

			tex.SetPixels(data);
			tex.Apply();

			return tex;
		}
		static void CopyFace(ref Color[] data, Cubemap map, CubemapFace face, int startX)
		{
			Color[] faceData = map.GetPixels(face);

			for (int y = 0; y < map.height; y++) {
				int yStride = y * map.width * 6;
				for (int x = 0; x < map.width; x++) {
					int i = startX + x + yStride;
					data[i] = faceData[x+((map.height-1)-y)*map.width];
				}
			}
		}
		#endregion
	}
}