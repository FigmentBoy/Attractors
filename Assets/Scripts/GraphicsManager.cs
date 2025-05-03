using System;
using System.Runtime.InteropServices;
using UnityEditor.SearchService;
using UnityEngine;

[ExecuteAlways, ImageEffectAllowedInSceneView]
public class GraphicsManager : MonoBehaviour
{
    [Header("Graphics Settings")]
    public bool useShaders = false;
    public bool useInSceneView = false;

    public float attractorStepSize = 1;
    public int maxAttractorSteps = 10;
    public int maxBounces = 10; 
    public int samplesPerPixel = 100;
    public float jitterStrength = 0.005f; // Strength of jittering for anti-aliasing
    [Range(0, 1)] public float environmentLightIntensity = 1.0f;
    
    [Header("BVH Settings")]
    [Range(1, 64)] public int BVHDepth = 10;
    public bool drawDebugLines = false; // Whether to draw debug lines for BVH nodes
    public bool regenerateBVHNodes = false;

    [Header("Shaders")]
    public Shader RTShader;
    public Shader accumulationShader;

    private Material RTMaterial;
    private Material accumulationMaterial;
    private RenderTexture resultTex;

    private ComputeBuffer sphereBuffer;
    private ComputeBuffer triangleBuffer;
    private ComputeBuffer vertexBuffer;
    private ComputeBuffer bvhBuffer;
    private ComputeBuffer attractorBuffer;

    private BVHNode[] bvhNodes;

    private int numFrames;

    void Start()
    {
        numFrames = 0;
        bvhNodes = null; // Remake the BVH on the first frame
    }

    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if ((!useInSceneView && !Application.isPlaying) || !useShaders) {
            // Skip rendering in scene view if not playing
            Graphics.Blit(source, destination);
            return;
        }

        InitializeShaders();
        InitializeSpheres();
        InitializeMeshes();
        InitializeAttractors();

        if (Application.isPlaying) {
            RenderTexture prevCopy = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
            Graphics.Blit(resultTex, prevCopy);

            RenderTexture currTex = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
            Graphics.Blit(null, currTex, RTMaterial);

            accumulationMaterial.SetInt("FrameCount", numFrames);
            accumulationMaterial.SetTexture("PrevTex", prevCopy);
            Graphics.Blit(currTex, resultTex, accumulationMaterial);
            Graphics.Blit(resultTex, destination);

            RenderTexture.ReleaseTemporary(currTex);
            RenderTexture.ReleaseTemporary(prevCopy);

            numFrames++;
        } else {
            Graphics.Blit(null, destination, RTMaterial);
        }
    }

    void OnValidate()
    {
        numFrames = 0;
    }

    void Update() {
        if (Input.GetKey(KeyCode.R)) {
            regenerateBVHNodes = true;
        }
    }

    void InitializeShaders()
    {
        if (RTShader == null) {
            Debug.LogError("Phong shader is not assigned in the GraphicsManager.");
            return;
        }

        if (accumulationShader == null) {
            Debug.LogError("Accumulation shader is not assigned in the GraphicsManager.");
            return;
        }

        if (RTMaterial == null) {
            RTMaterial = new Material(RTShader);
            RTMaterial.hideFlags = HideFlags.HideAndDontSave;
        }

        if (accumulationMaterial == null) {
            accumulationMaterial = new Material(accumulationShader);
            accumulationMaterial.hideFlags = HideFlags.HideAndDontSave;
        }

        bool cameraMoving = Camera.current != null && Camera.current.transform.hasChanged;
        if (resultTex == null || resultTex.width != Screen.width || resultTex.height != Screen.height || cameraMoving) {
            if (resultTex != null) {
                resultTex.Release();
            }
            resultTex = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat);
            resultTex.enableRandomWrite = true;
            resultTex.autoGenerateMips = false;
            resultTex.Create();

            if (Camera.current != null) {
                Camera.current.transform.hasChanged = false;
            }

            numFrames = 0;
        }

        Camera cam = Camera.current;
        
        float planeHeight = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2;
		float planeWidth = planeHeight * cam.aspect;

		// Send data to shader
		RTMaterial.SetVector("CamParams", new Vector3(planeWidth, planeHeight, 1));
		RTMaterial.SetMatrix("LocalWorldMatrix", cam.transform.localToWorldMatrix);

		RTMaterial.SetFloat("StepSize", attractorStepSize);
		RTMaterial.SetFloat("MaxSteps", maxAttractorSteps);

		RTMaterial.SetInt("MaxBounces", maxBounces);
		RTMaterial.SetInt("SamplesPerPixel", samplesPerPixel);
        RTMaterial.SetInt("CurrentFrame", numFrames);
        RTMaterial.SetFloat("JitterStrength", jitterStrength);

        RTMaterial.SetFloat("EnvironmentLightIntensity", environmentLightIntensity);
    }

    void InitializeMeshes()
    {
        if (bvhNodes != null && !regenerateBVHNodes) {
            return;
        }

        regenerateBVHNodes = false;

        RTMesh[] RTMeshes = FindObjectsByType<RTMesh>(FindObjectsSortMode.None);
        if (RTMeshes == null || RTMeshes.Length == 0) {
            bvhBuffer?.Release();
            bvhBuffer = null;

            triangleBuffer?.Release();
            triangleBuffer = null;

            vertexBuffer?.Release();
            vertexBuffer = null;

            RTMaterial.SetInt("BVHNodeCount", 0);
            return;
        }

        // Get total num of triangles and vertices
        int totalTriangles = 0;
        int totalVertices = 0;

        foreach (RTMesh mesh in RTMeshes) {
            if (mesh.TryGetComponent<MeshFilter>(out MeshFilter meshFilter)) {
                totalTriangles += meshFilter.sharedMesh.triangles.Length / 3;
                totalVertices += meshFilter.sharedMesh.vertexCount;
            }
        }

        Triangle[] triangles = new Triangle[totalTriangles];
        Vertex[] vertices = new Vertex[totalVertices];

        int triangleIndex = 0;
        int vertexIndex = 0;

        for (int i = 0; i < RTMeshes.Length; i++) {
            RTMesh mesh = RTMeshes[i];
            if (mesh.TryGetComponent<MeshFilter>(out MeshFilter meshFilter)) {
                Mesh m = meshFilter.sharedMesh;
                if (m == null) continue;

                int triangleBase = vertexIndex;

                for (int j = 0; j < m.subMeshCount; j++) {
                    int[] subTriangles = m.GetTriangles(j);

                    for (int k = 0; k < subTriangles.Length; k += 3) {
                        triangles[triangleIndex++] = new Triangle {
                            a = subTriangles[k] + triangleBase,
                            b = subTriangles[k + 1] + triangleBase,
                            c = subTriangles[k + 2] + triangleBase,
                            isDoubleSided = mesh.flipNormals ? 0 : 1,
                            material = mesh.material
                        };
                    }
                }

                Vector3[] subVertices = m.vertices;

                for (int k = 0; k < subVertices.Length; k++) {
                    Vector3 worldPosition = mesh.transform.TransformPoint(subVertices[k]);
                    Vector3 worldNormal = mesh.transform.TransformDirection(mesh.flipNormals ? -m.normals[k] : m.normals[k]);

                    vertices[vertexIndex++] = new Vertex {
                        position = worldPosition,
                        normal = worldNormal
                    };
                }
            }
        }

        bvhNodes = BVH.BuildBVH(triangles, vertices, BVHDepth); // Build the BVH tree for the triangles

        if (bvhBuffer == null || bvhBuffer.count != bvhNodes.Length || !bvhBuffer.IsValid()) {
            bvhBuffer?.Release();
            bvhBuffer = new ComputeBuffer(bvhNodes.Length, Marshal.SizeOf<BVHNode>(), ComputeBufferType.Structured);
        }
        bvhBuffer.SetData(bvhNodes);
        RTMaterial.SetBuffer("BVHNodes", bvhBuffer);
        RTMaterial.SetInt("BVHNodeCount", bvhNodes.Length);

        if (drawDebugLines) {
            for (int i = 0; i < bvhNodes.Length; i++) {
                BVHNode node = bvhNodes[i];
                Color color = Color.Lerp(Color.blue, Color.green, (float)i / bvhNodes.Length);
                Debug.DrawLine(node.min, new Vector3(node.min.x, node.min.y, node.max.z), color, 10f);
                Debug.DrawLine(node.min, new Vector3(node.min.x, node.max.y, node.min.z), color, 10f);
                Debug.DrawLine(node.max, new Vector3(node.max.x, node.max.y, node.min.z), color, 10f);
                Debug.DrawLine(node.max, new Vector3(node.max.x, node.min.y, node.max.z), color, 10f);
                Debug.DrawLine(new Vector3(node.min.x, node.min.y, node.max.z), new Vector3(node.max.x, node.min.y, node.max.z), color, 10f);
                Debug.DrawLine(new Vector3(node.min.x, node.max.y, node.min.z), new Vector3(node.max.x, node.max.y, node.min.z), color, 10f);
                Debug.DrawLine(new Vector3(node.min.x, node.max.y, node.max.z), new Vector3(node.max.x, node.max.y, node.max.z), color, 10f);
                Debug.DrawLine(new Vector3(node.min.x, node.min.y, node.min.z), new Vector3(node.max.x, node.min.y, node.min.z), color, 10f);
                Debug.DrawLine(new Vector3(node.min.x, node.max.y, node.max.z), new Vector3(node.min.x, node.max.y, node.min.z), color, 10f);
                Debug.DrawLine(new Vector3(node.max.x, node.min.y, node.max.z), new Vector3(node.max.x, node.max.y, node.max.z), color, 10f);
                Debug.DrawLine(new Vector3(node.min.x, node.min.y, node.min.z), new Vector3(node.min.x, node.max.y, node.min.z), color, 10f);
                Debug.DrawLine(new Vector3(node.max.x, node.min.y, node.min.z), new Vector3(node.max.x, node.max.y, node.min.z), color, 10f);
            }
        }

        if (triangleBuffer == null || triangleBuffer.count != triangles.Length || !triangleBuffer.IsValid()) {
            triangleBuffer?.Release();
            triangleBuffer = new ComputeBuffer(triangles.Length, Marshal.SizeOf<Triangle>(), ComputeBufferType.Structured);
        }
        triangleBuffer.SetData(triangles);
        RTMaterial.SetBuffer("Triangles", triangleBuffer);

        if (vertexBuffer == null || vertexBuffer.count != vertices.Length || !vertexBuffer.IsValid()) {
            vertexBuffer?.Release();
            vertexBuffer = new ComputeBuffer(vertices.Length, Marshal.SizeOf<Vertex>(), ComputeBufferType.Structured);
        }
        vertexBuffer.SetData(vertices);
        RTMaterial.SetBuffer("Vertices", vertexBuffer);
    }

    void InitializeSpheres()
    {
        RTSphere[] RTSpheres = FindObjectsByType<RTSphere>(FindObjectsSortMode.None);

        if (RTSpheres.Length == 0) {
            RTMaterial.SetInt("SphereCount", 0);
            return;
        }

        Sphere[] spheres = new Sphere[RTSpheres.Length];

        for (int i = 0; i < RTSpheres.Length; i++) {
            spheres[i] = new Sphere {
                position = RTSpheres[i].transform.position,
                radius = RTSpheres[i].transform.localScale.x * 0.5f,
                material = RTSpheres[i].material
            };
        }
        
        if (sphereBuffer == null || sphereBuffer.count != spheres.Length || !sphereBuffer.IsValid()) {
            sphereBuffer?.Release();
            sphereBuffer = new ComputeBuffer(spheres.Length, Marshal.SizeOf<Sphere>(), ComputeBufferType.Structured);
        }

        sphereBuffer.SetData(spheres);
        RTMaterial.SetBuffer("Spheres", sphereBuffer);
        RTMaterial.SetInt("SphereCount", spheres.Length);
    }

    void InitializeAttractors()
    {
        RTAttractor[] RTAttractors = FindObjectsByType<RTAttractor>(FindObjectsSortMode.None);
        if (RTAttractors.Length == 0) {
            RTMaterial.SetInt("AttractorCount", 0);
            return;
        }

        Attractor[] attractors = new Attractor[RTAttractors.Length];

        for (int i = 0; i < RTAttractors.Length; i++) {
            // We are in the event horizon if the acceleration (strength/distance^2) is more than the
            // acceleration needed for a circular orbit, AKA when strength/r^2 > v^2/r (where v is StepSize)
            // r < strength / (StepSize^2)

            attractors[i] = new Attractor {
                position = RTAttractors[i].transform.position,
                strength = RTAttractors[i].strength,
                radius = RTAttractors[i].strength / 10, // This looks better than what was commented out
            };
        }

        if (attractorBuffer == null || attractorBuffer.count != attractors.Length || !attractorBuffer.IsValid()) {
            attractorBuffer?.Release();
            attractorBuffer = new ComputeBuffer(attractors.Length, Marshal.SizeOf<Attractor>(), ComputeBufferType.Structured);
        }

        attractorBuffer.SetData(attractors);

        RTMaterial.SetBuffer("Attractors", attractorBuffer);
        RTMaterial.SetInt("AttractorCount", attractors.Length);
    }
}
