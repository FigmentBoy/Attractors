Shader "Custom/RayTracer" {
    SubShader {
        Cull Off ZWrite Off ZTest Always

        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            #define EPSILON 0.001
            #define PI 3.14159265358979323846

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            struct RTMaterial {
                float4 albedo;
                float4 specularColor;
                float specularProbability;
                float4 emissionColor;
                float emissiveStrength;
                float roughness;
            };

            struct Sphere {
                float3 center;
                float radius;
                RTMaterial material;
            };

            struct Ray {
                float3 origin;
                float3 direction;
            };

            struct HitInfo {
                bool hit;
                float t;
                float3 location;
                float3 normal;
                bool doubleSided;
                RTMaterial material;
            };

            struct Triangle {
                int3 indices;
                int isDoubleSided;
                RTMaterial material;
            };

            struct VertexData {
                float3 position;
                float3 normal;
            };

            struct BVHNode {
                float3 min;
                float3 max;
                int leftChild; // -1 if no child
                int rightChild; // -1 if no child
                int triangleIndex; // -1 if this is an internal node
                int triangleCount; // 0 if this is an internal node
            };

            struct Attractor {
                float3 position;
                float strength;
                float radius;
            };

            // Uniforms
            float3 CamParams;
            float4x4 LocalWorldMatrix;

            int StepSize;
            int MaxSteps;

            int MaxBounces;
            int SamplesPerPixel;
            int CurrentFrame;
            float JitterStrength;

            float EnvironmentLightIntensity;

            StructuredBuffer<Sphere> Spheres;
            int SphereCount;

            StructuredBuffer<Triangle> Triangles;
            StructuredBuffer<VertexData> Vertices;
            StructuredBuffer<BVHNode> BVHNodes;
            int BVHNodeCount;

            StructuredBuffer<Attractor> Attractors;
            int AttractorCount;

            // Randomness
            uint state;

            uint PcgNextUInt() {
                state = state * 747796405 + 2891336453;
				uint result = ((state >> ((state >> 28) + 4)) ^ state) * 277803737;
				result = (result >> 22) ^ result;
				return result;
            }

            float RandomFloat() {
                const float maxUInt = 4294967295.0;
                return PcgNextUInt() / maxUInt; // Normalize to [0, 1)
            }

            float NormalDistRandomFloat() {
                // Box-Muller transform for normal distribution
                float u1 = RandomFloat();
                float u2 = RandomFloat();
                return sqrt(-2.0 * log(u1)) * cos(2.0 * PI * u2);
            }

            float3 RandomUnitVector() {
                // https://math.stackexchange.com/a/1585996
                float x = NormalDistRandomFloat();
                float y = NormalDistRandomFloat();
                float z = NormalDistRandomFloat();
                return normalize(float3(x, y, z));
            }

            float2 RandomCirclePoint(float radius) {
                // Generate a random point on a circle with the given radius
                float angle = RandomFloat() * 2.0 * PI;
                return float2(cos(angle), sin(angle)) * sqrt(RandomFloat()) * radius;
            }

            // Vertex shader
            v2f vert(appdata v) {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float GenericRaySphere(Ray ray, float3 sphereCenter, float sphereRadius) {
                float3 oc = ray.origin - sphereCenter; // Vector from ray origin to sphere center
                float a = dot(ray.direction, ray.direction);
                float b = 2.0 * dot(oc, ray.direction); // Coefficient for the quadratic equation
                float c = dot(oc, oc) - sphereRadius * sphereRadius; // Constant term

                float discriminant = b * b - 4 * a * c; // Discriminant of the quadratic equation
                if (discriminant < 0) {
                    return 1.#INF; // No intersection
                }

                return (-b - sqrt(discriminant)) / (2.0 * a); // Solve for t (the distance along the ray)
            }

            // Function to check if a ray intersects with a sphere
            HitInfo IntersectRaySphere(Ray ray, Sphere sphere) {
                HitInfo hitInfo;
                hitInfo.hit = false;
                hitInfo.doubleSided = false; // Default to not double-sided

                float t = GenericRaySphere(ray, sphere.center, sphere.radius); // Get the intersection distance
                if (t < EPSILON || t >= 1.#INF) {
                    return hitInfo; // No intersection or intersection behind the ray origin
                }

                hitInfo.hit = true;
                hitInfo.t = t;
                hitInfo.location = ray.origin + t * ray.direction;
                hitInfo.normal = normalize(hitInfo.location - sphere.center);
                hitInfo.material = sphere.material;

                return hitInfo;
            }

            float IntersectRayAABB(Ray ray, BVHNode node) {
                float tx1 = (node.min.x - ray.origin.x) / ray.direction.x;
                float tx2 = (node.max.x - ray.origin.x) / ray.direction.x;
                float ty1 = (node.min.y - ray.origin.y) / ray.direction.y;
                float ty2 = (node.max.y - ray.origin.y) / ray.direction.y;
                float tz1 = (node.min.z - ray.origin.z) / ray.direction.z;
                float tz2 = (node.max.z - ray.origin.z) / ray.direction.z;
                float tmin = max(max(min(tx1, tx2), min(ty1, ty2)), min(tz1, tz2)); // Find the maximum of the minimums
                float tmax = min(min(max(tx1, tx2), max(ty1, ty2)), max(tz1, tz2)); // Find the minimum of the maximums

                return tmax > 0 && tmin <= tmax ? tmin : 1.#INF; // Return the hit information
            }

            HitInfo IntersectRayTriangle(Ray ray, Triangle tri) {
                HitInfo hitInfo;
                hitInfo.hit = false;

                VertexData v0 = Vertices[tri.indices.x];
                VertexData v1 = Vertices[tri.indices.y];
                VertexData v2 = Vertices[tri.indices.z];

                float3 edge1 = v1.position - v0.position;
                float3 edge2 = v2.position - v0.position;
                float3 h = cross(ray.direction, edge2);
                float a = dot(edge1, h);

                if (abs(a) < EPSILON) {
                    return hitInfo; // Ray is parallel to the triangle
                }

                float f = 1.0 / a;
                float3 s = ray.origin - v0.position;
                float u = f * dot(s, h);

                if (u < 0.0 || u > 1.0) {
                    return hitInfo; // Intersection outside the triangle
                }

                float3 q = cross(s, edge1);
                float v = f * dot(ray.direction, q);

                if (v < 0.0 || u + v > 1.0) {
                    return hitInfo; // Intersection outside the triangle
                }

                // Calculate t to find the intersection point
                float t = f * dot(edge2, q);

                if (t < EPSILON) {
                    return hitInfo; // Intersection behind the ray origin
                }

                hitInfo.hit = true;
                hitInfo.t = t;
                hitInfo.location = ray.origin + t * ray.direction;

                // Calculate normal using barycentric coordinates
                hitInfo.normal = normalize(v0.normal * (1 - u - v) + v1.normal * u + v2.normal * v);
                hitInfo.material = tri.material;
                hitInfo.doubleSided = tri.isDoubleSided == 1;

                return hitInfo;
            }

            void RayScene(Ray ray, out HitInfo closestHit) {
                for (int step = 0; step < MaxSteps; step++) {
                    if (AttractorCount == 0) {
                        step = MaxSteps - 1;
                    }

                    closestHit.hit = false; // Reset hit info for each step
                    closestHit.t = 1.#INF;

                    if (step != MaxSteps - 1) {
                        closestHit.t = StepSize; // Only go at most StepSize distance per step

                        for (int i = 0; i < AttractorCount; i++) {
                            Attractor attractor = Attractors[i];
                            float3 toAttractor = attractor.position - ray.origin;
                            float distanceSquared = dot(toAttractor, toAttractor);

                            ray.direction += normalize(toAttractor) * attractor.strength / distanceSquared;
                            ray.direction = normalize(ray.direction);
                        }
                    }

                    for (int j = 0; j < SphereCount; j++) {
                        HitInfo hitInfo = IntersectRaySphere(ray, Spheres[j]);
                        if (hitInfo.hit && (hitInfo.doubleSided || dot(ray.direction, hitInfo.normal) < 0)) {
                            if (hitInfo.t < closestHit.t) {
                                closestHit = hitInfo;
                            }
                        }
                    }

                    if (BVHNodeCount != 0) {
                        int stack[64]; // Stack for BVH traversal
                        int stackIndex = 0;

                        float rootHit = IntersectRayAABB(ray, BVHNodes[0]);
                        if (rootHit < closestHit.t) {
                            stack[stackIndex++] = 0; // Start with the root node if it intersects
                        }

                        while (stackIndex > 0) {
                            int nodeIndex = stack[--stackIndex]; // Pop the top node from the stack
                            BVHNode node = BVHNodes[nodeIndex];

                            if (node.triangleIndex >= 0 && node.triangleCount > 0) { // Leaf node, check triangles
                                for (int i = 0; i < node.triangleCount; i++) {
                                    Triangle tri = Triangles[node.triangleIndex + i];
                                    HitInfo hitInfo = IntersectRayTriangle(ray, tri);
                                    if (hitInfo.hit && (hitInfo.doubleSided || dot(ray.direction, hitInfo.normal) < 0)) { // Check if the hit is valid and not too close
                                        if (hitInfo.t < closestHit.t) {
                                            closestHit = hitInfo;
                                        }
                                    }
                                }
                            } else { // Internal node, push children onto the stack
                                float leftHit = IntersectRayAABB(ray, BVHNodes[node.leftChild]);
                                float rightHit = IntersectRayAABB(ray, BVHNodes[node.rightChild]);

                                bool leftIndexSmaller = node.leftChild < node.rightChild; // Check which child has a smaller index
                                
                                float nearChildHit = leftIndexSmaller ? leftHit : rightHit;
                                float farChildHit = leftIndexSmaller ? rightHit : leftHit;

                                int nearChildIndex = leftIndexSmaller ? node.leftChild : node.rightChild;
                                int farChildIndex = leftIndexSmaller ? node.rightChild : node.leftChild;

                                
                                if (farChildHit < 1.#INF && farChildHit < closestHit.t) {
                                    stack[stackIndex++] = farChildIndex; // Push far child if it intersects
                                }

                                if (nearChildHit < 1.#INF && nearChildHit < closestHit.t) {
                                    stack[stackIndex++] = nearChildIndex; // Push near child if it intersects
                                }
                            }
                        }
                    }

                    for (int i = 0; i < AttractorCount; i++) {
                        Attractor attractor = Attractors[i];
                        float res = GenericRaySphere(ray, attractor.position, attractor.radius);

                        if (res < closestHit.t && res < 1.#INF) {
                            closestHit.hit = true; // We are in the event horizon of an attractor
                            closestHit.t = -1;
                            closestHit.location = ray.origin + res * ray.direction;
                            closestHit.normal = normalize(ray.origin - attractor.position);
                            closestHit.material.albedo = attractor.strength > 0 ? float4(0,0,0,1) : (1,1,1,1); // White for repeller, black for attractor
                            closestHit.material.specularProbability = 0.0; // No specular reflection for attractors
                            return; 
                        }
                    }

                    if (closestHit.hit) {
                        break;
                    }

                    ray.origin += ray.direction * StepSize;
                }

                return;
            }

            float3 EnvironmentColor(float3 direction) {
                float a = 0.5 * (direction.y + 1.0); // Simple gradient sky color
                return lerp(float3(0.5, 0.5, 0.5), float3(0.5, 0.7, 1.0), a) * EnvironmentLightIntensity;
            }

            float3 RayTrace(Ray ray) {
                float3 rayColor = float3(1.0, 1.0, 1.0);
                float3 light = float3(0.0, 0.0, 0.0);

                for (int bounce = 0; bounce < MaxBounces; bounce++) {
                    HitInfo hitInfo;
                    RayScene(ray, hitInfo);

                    if (!hitInfo.hit) {
                        light += EnvironmentColor(ray.direction) * rayColor;
                        break;
                    }

                    if (hitInfo.t < 0) { // If we are in the event horizon of an attractor
                        if (bounce == 0) {
                            light = hitInfo.material.albedo; // Attractors have a specific color
                        } 
                        
                        break;
                    }

                    light += hitInfo.material.emissionColor * hitInfo.material.emissiveStrength * rayColor;

                    ray.origin = hitInfo.location;

                    bool specularBounce = RandomFloat() < hitInfo.material.specularProbability;
                    if (specularBounce) {
                        float3 specularDir = reflect(ray.direction, hitInfo.normal);
                        specularDir += RandomUnitVector() * hitInfo.material.roughness; // Add some randomness to the specular direction
                        ray.direction = normalize(specularDir);
                        rayColor *= hitInfo.material.specularColor.rgb;
                    } else {
                        float3 diffuseDir = normalize(hitInfo.normal + RandomUnitVector());
                        ray.direction = diffuseDir;
                        rayColor *= hitInfo.material.albedo.rgb;
                    }
                    
                    float factor = max(rayColor.r, max(rayColor.g, rayColor.b));

                    // Randomly kill some rays
                    if (RandomFloat() >= factor) {
                        break;
                    }

                    rayColor /= max(rayColor.r, max(rayColor.g, rayColor.b));
                }

                return light;
            }

            fixed4 frag (v2f i) : SV_Target {
                // Convert UV to pixel coordinates
                float2 pixel = i.uv * _ScreenParams.xy;

                // Use pixel coordinates and frame to seed the random state
                state = (pixel.y * CamParams.y * pixel.x); 
                state = state + (CurrentFrame * 0xDEADBEEF);

                float3 cameraPosition = _WorldSpaceCameraPos;                 
                float3 right = LocalWorldMatrix[0].xyz;
                float3 up = LocalWorldMatrix[1].xyz;

                float3 localDir = float3(i.uv - 0.5, 1) * CamParams;
                float3 focus = mul(LocalWorldMatrix, float4(localDir, 1));

                float4 color = float4(0.0, 0.0, 0.0, 1.0);

                for (int i = 0; i < SamplesPerPixel; i++) {
                    Ray ray;
                
                    ray.origin = cameraPosition;

                    // Adjust the ray direction with random jitter
                    float2 jitter = RandomCirclePoint(JitterStrength) / CamParams.xy;
                    float3 jitteredFocus = focus + jitter.x * right + jitter.y * up;
                    ray.direction = normalize(jitteredFocus - cameraPosition);
                
                    color.rgb += RayTrace(ray);    
                }

                return color / SamplesPerPixel;
            }
            ENDCG
        }
    }
}
