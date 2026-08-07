/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 */
using LoneEftDmaRadar.Tarkov.IL2CPP;
using LoneEftDmaRadar.Tarkov.Unity.Structures;
using VmmSharpEx.Extensions;
using VmmSharpEx.Scatter;

namespace LoneEftDmaRadar.Tarkov.World
{
    /// <summary>
    /// Reads the live EFT camera projection through DMA for Aimview rendering.
    /// Camera resolution is attempted through the EFT CameraManager singleton first,
    /// then Unity's AllCameras list. Aimview retains its synthetic-camera fallback.
    /// </summary>
    public sealed class CameraManager
    {
        private const float NEAR_CLIP_W = 0.098f;
        private const float JITTER_BASELINE_ALPHA = 0.01f;

        private readonly ulong _fpsCamera;
        private readonly ulong _opticCamera;
        private ProjectionSnapshot _snapshot;
        private float _fov = 75f;
        private float _aspect = 16f / 9f;
        private float _jitterBaselineX;
        private float _jitterBaselineY;
        private bool _jitterBaselineInitialized;

        public bool IsReady => Volatile.Read(ref _snapshot) is not null;

        public CameraManager()
        {
            if (!TryResolveViaEftCameraManager(out _fpsCamera, out _opticCamera) &&
                !TryResolveViaAllCameras(out _fpsCamera, out _opticCamera))
            {
                throw new InvalidOperationException("Unable to resolve the EFT FPS camera.");
            }

            Logging.WriteLine($"[CameraManager] FPS Camera: 0x{_fpsCamera:X}; Optic Camera: 0x{_opticCamera:X}");
        }

        /// <summary>
        /// Queues all camera state in the same realtime scatter batch as player transforms.
        /// </summary>
        public void OnRealtimeLoop(VmmScatter scatter)
        {
            ulong fpsMatrixAddr = _fpsCamera + UnityOffsets.Camera_ViewMatrix;
            ulong fovAddr = _fpsCamera + UnityOffsets.Camera_FOV;
            ulong aspectAddr = _fpsCamera + UnityOffsets.Camera_AspectRatio;

            scatter.PrepareReadValue<Matrix4x4>(fpsMatrixAddr);
            scatter.PrepareReadValue<float>(fovAddr);
            scatter.PrepareReadValue<float>(aspectAddr);

            ulong opticMatrixAddr = 0;
            ulong opticActiveAddr = 0;
            if (_opticCamera.IsValidUserVA())
            {
                opticMatrixAddr = _opticCamera + UnityOffsets.Camera_ViewMatrix;
                opticActiveAddr = _opticCamera + UnityOffsets.Camera_IsAddedOffset;
                scatter.PrepareReadValue<Matrix4x4>(opticMatrixAddr);
                scatter.PrepareReadValue<bool>(opticActiveAddr);
            }

            scatter.Completed += (_, result) =>
            {
                if (result.ReadValue<float>(fovAddr, out var fov) && fov is > 1f and < 180f)
                    _fov = fov;
                if (result.ReadValue<float>(aspectAddr, out var aspect) && aspect is > 0.1f and < 5f)
                    _aspect = aspect;

                bool isScoped = opticActiveAddr != 0 &&
                    result.ReadValue<bool>(opticActiveAddr, out var opticActive) && opticActive;

                Matrix4x4 matrix;
                if (isScoped &&
                    result.ReadValue<Matrix4x4>(opticMatrixAddr, out var opticMatrix) &&
                    IsValidMatrix(in opticMatrix))
                {
                    matrix = opticMatrix;
                }
                else if (result.ReadValue<Matrix4x4>(fpsMatrixAddr, out var fpsMatrix) &&
                    IsValidMatrix(in fpsMatrix))
                {
                    matrix = fpsMatrix;
                    isScoped = false;
                }
                else
                {
                    return;
                }

                UpdateSnapshot(in matrix, isScoped);
            };
        }

        /// <summary>
        /// Projects a world position directly into the Aimview viewport using the live game matrix.
        /// </summary>
        public bool WorldToScreen(in Vector3 world, int width, int height, out SKPoint screen)
        {
            screen = default;
            var snapshot = Volatile.Read(ref _snapshot);
            if (snapshot is null || world.LengthSquared() < 1f)
                return false;

            float w = Vector3.Dot(snapshot.Translation, world) + snapshot.M44;
            if (!float.IsFinite(w) || w < NEAR_CLIP_W)
                return false;

            float x = Vector3.Dot(snapshot.Right, world) + snapshot.M14;
            float y = Vector3.Dot(snapshot.Up, world) + snapshot.M24;

            x += snapshot.JitterX * w;
            y += snapshot.JitterY * w;

            if (snapshot.IsScoped)
            {
                float halfFov = snapshot.Fov * (MathF.PI / 180f) * 0.5f;
                float sin = MathF.Sin(halfFov);
                if (MathF.Abs(sin) > 0.0001f)
                {
                    float cotangent = MathF.Cos(halfFov) / sin;
                    x /= cotangent * snapshot.Aspect * 0.5f;
                    y /= cotangent * 0.5f;
                }
            }

            screen.X = width * 0.5f * (1f + x / w);
            screen.Y = height * 0.5f * (1f - y / w);
            return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
        }

        private void UpdateSnapshot(in Matrix4x4 matrix, bool isScoped)
        {
            var translation = new Vector3(matrix.M14, matrix.M24, matrix.M34);
            var right = new Vector3(matrix.M11, matrix.M21, matrix.M31);
            var up = new Vector3(matrix.M12, matrix.M22, matrix.M32);

            float jitterX = 0f;
            float jitterY = 0f;
            float forwardLengthSquared = translation.LengthSquared();
            if (forwardLengthSquared > 1e-12f)
            {
                float rawX = -Vector3.Dot(right, translation) / forwardLengthSquared;
                float rawY = -Vector3.Dot(up, translation) / forwardLengthSquared;
                if (!_jitterBaselineInitialized)
                {
                    _jitterBaselineX = rawX;
                    _jitterBaselineY = rawY;
                    _jitterBaselineInitialized = true;
                }
                else
                {
                    _jitterBaselineX += JITTER_BASELINE_ALPHA * (rawX - _jitterBaselineX);
                    _jitterBaselineY += JITTER_BASELINE_ALPHA * (rawY - _jitterBaselineY);
                    jitterX = rawX - _jitterBaselineX;
                    jitterY = rawY - _jitterBaselineY;
                }
            }

            Volatile.Write(ref _snapshot, new ProjectionSnapshot
            {
                M44 = matrix.M44,
                M14 = matrix.M41,
                M24 = matrix.M42,
                Translation = translation,
                Right = right,
                Up = up,
                JitterX = jitterX,
                JitterY = jitterY,
                Fov = _fov,
                Aspect = _aspect,
                IsScoped = isScoped
            });
        }

        private static bool TryResolveViaEftCameraManager(out ulong fpsCamera, out ulong opticCamera)
        {
            fpsCamera = 0;
            opticCamera = 0;
            try
            {
                ulong instance = FindEftCameraManagerInstance();

                ulong fpsCameraRef = Memory.ReadPtr(instance + Offsets.EFTCameraManager.Camera, false);
                fpsCamera = Memory.ReadPtr(fpsCameraRef + ObjectClass.MonoBehaviourOffset, false);
                if (!IsValidCamera(fpsCamera))
                {
                    fpsCamera = 0;
                    return false;
                }

                try
                {
                    ulong opticManager = Memory.ReadPtr(instance + Offsets.EFTCameraManager.OpticCameraManager, false);
                    ulong opticCameraRef = Memory.ReadPtr(opticManager + Offsets.OpticCameraManager.Camera, false);
                    opticCamera = Memory.ReadPtr(opticCameraRef + ObjectClass.MonoBehaviourOffset, false);
                    if (!IsValidCamera(opticCamera))
                        opticCamera = 0;
                }
                catch
                {
                    opticCamera = 0;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[CameraManager] EFT CameraManager RVA lookup failed: {ex.Message}");
                fpsCamera = 0;
                opticCamera = 0;
                return false;
            }
        }

        /// <summary>
        /// Resolves CameraManager.Instance from get_Instance machine code. This path does not
        /// depend on IL2CPPLib's type-definition table, which is optional in the current build.
        /// </summary>
        private static ulong FindEftCameraManagerInstance()
        {
            ulong method = Memory.GameAssemblyBase + Offsets.EFTCameraManager.GetInstance_RVA;
            method.ThrowIfInvalidUserVA(nameof(method));

            Span<byte> code = stackalloc byte[128];
            Memory.ReadSpan(method, code, false);

            // lea rcx,[rip+rel32] -> address containing Il2CppClass*
            for (int i = 0; i <= code.Length - 7; i++)
            {
                if (code[i] != 0x48 || code[i + 1] != 0x8D || code[i + 2] != 0x0D)
                    continue;

                int displacement = BitConverter.ToInt32(code.Slice(i + 3, 4));
                ulong classAddress = method.AddRVA((uint)i + 7, displacement);
                if (TryReadCameraManagerInstanceFromClassAddress(classAddress, out var instance))
                    return instance;
            }

            // Some builds use mov reg,[rip+rel32] for the same class/static storage.
            for (int i = 0; i <= code.Length - 7; i++)
            {
                bool isMovRax = code[i] == 0x48 && code[i + 1] == 0x8B && code[i + 2] == 0x05;
                bool isMovRcx = code[i] == 0x48 && code[i + 1] == 0x8B && code[i + 2] == 0x0D;
                if (!isMovRax && !isMovRcx)
                    continue;

                int displacement = BitConverter.ToInt32(code.Slice(i + 3, 4));
                ulong storageAddress = method.AddRVA((uint)i + 7, displacement);
                if (TryReadCameraManagerInstanceFromClassAddress(storageAddress, out var instance))
                    return instance;
            }

            throw new InvalidOperationException("get_Instance did not expose a valid CameraManager singleton.");
        }

        private static bool TryReadCameraManagerInstanceFromClassAddress(ulong classAddress, out ulong instance)
        {
            instance = 0;
            try
            {
                ulong classPtr = Memory.ReadPtr(classAddress, false);
                if (!classPtr.IsValidUserVA())
                    return false;

                var cameraClass = Memory.ReadValue<IL2CPPLib.Class>(classPtr, false);
                if (!cameraClass.static_fields.IsValidUserVA())
                    return false;

                ulong candidate = Memory.ReadPtr(cameraClass.static_fields, false);
                ulong cameraRef = Memory.ReadPtr(candidate + Offsets.EFTCameraManager.Camera, false);
                ulong camera = Memory.ReadPtr(cameraRef + ObjectClass.MonoBehaviourOffset, false);
                if (!IsValidCamera(camera))
                    return false;

                instance = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveViaAllCameras(out ulong fpsCamera, out ulong opticCamera)
        {
            fpsCamera = 0;
            opticCamera = 0;
            try
            {
                ulong allCamerasAddress = ResolveAllCamerasAddress();
                ulong list = Memory.ReadPtr(allCamerasAddress, false);
                ulong items = Memory.ReadPtr(list, false);
                int count = Memory.ReadValue<int>(list + 0x8, false);
                if (!items.IsValidUserVA() || count is <= 0 or > 1024)
                    return false;

                for (int i = 0; i < Math.Min(count, 100); i++)
                {
                    try
                    {
                        ulong camera = Memory.ReadPtr(items + (uint)(i * 8), false);
                        ulong gameObject = Memory.ReadPtr(camera + UnityOffsets.GameObject_ObjectClassOffset, false);
                        ulong namePtr = Memory.ReadPtr(gameObject + UnityOffsets.GameObject_NameOffset, false);
                        string name = Memory.ReadUtf8String(namePtr, 64, false);

                        if (fpsCamera == 0 &&
                            name.Contains("FPS", StringComparison.OrdinalIgnoreCase) &&
                            name.Contains("Camera", StringComparison.OrdinalIgnoreCase) && IsValidCamera(camera))
                        {
                            fpsCamera = camera;
                        }
                        else if (opticCamera == 0 &&
                            (name.Contains("Optic", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("BaseOptic", StringComparison.OrdinalIgnoreCase)) &&
                            name.Contains("Camera", StringComparison.OrdinalIgnoreCase) && IsValidCamera(camera))
                        {
                            opticCamera = camera;
                        }
                    }
                    catch { }

                    if (fpsCamera != 0 && opticCamera != 0)
                        break;
                }

                return fpsCamera != 0;
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"[CameraManager] AllCameras lookup failed: {ex.Message}");
                return false;
            }
        }

        private static ulong ResolveAllCamerasAddress()
        {
            string[] signatures =
            [
                "48 8B 05 ? ? ? ? 49 C7 C6 ? ? ? ? 8B 48 ? 85 C9 0F 84 ? ? ? ? 48 89 9C 24",
                "4C 8B 05 ? ? ? ? 33 D2 49 8B 48"
            ];

            foreach (string signature in signatures)
            {
                try
                {
                    ulong hit = Memory.FindSignature(signature);
                    if (!hit.IsValidUserVA())
                        continue;
                    int displacement = Memory.ReadValue<int>(hit + 3, false);
                    ulong candidate = hit.AddRVA(7, displacement);
                    if (ValidateAllCamerasAddress(candidate))
                        return candidate;
                }
                catch { }
            }

            ulong fallback = Memory.UnityBase + UnityOffsets.AllCameras;
            if (ValidateAllCamerasAddress(fallback))
                return fallback;
            throw new InvalidOperationException("Unity AllCameras address is unavailable.");
        }

        private static bool ValidateAllCamerasAddress(ulong address)
        {
            try
            {
                ulong list = Memory.ReadPtr(address, false);
                ulong items = Memory.ReadPtr(list, false);
                int count = Memory.ReadValue<int>(list + 0x8, false);
                return items.IsValidUserVA() && count is >= 0 and < 1024;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidCamera(ulong camera)
        {
            if (!camera.IsValidUserVA())
                return false;
            try
            {
                var matrix = Memory.ReadValue<Matrix4x4>(camera + UnityOffsets.Camera_ViewMatrix, false);
                return IsValidMatrix(in matrix);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsValidMatrix(in Matrix4x4 matrix)
        {
            return float.IsFinite(matrix.M11) && float.IsFinite(matrix.M22) &&
                float.IsFinite(matrix.M33) && float.IsFinite(matrix.M44) &&
                !(matrix.M11 == 0f && matrix.M22 == 0f && matrix.M33 == 0f && matrix.M44 == 0f) &&
                MathF.Abs(matrix.M41) < 5000f && MathF.Abs(matrix.M42) < 5000f && MathF.Abs(matrix.M43) < 5000f;
        }

        private sealed class ProjectionSnapshot
        {
            public float M44 { get; init; }
            public float M14 { get; init; }
            public float M24 { get; init; }
            public Vector3 Translation { get; init; }
            public Vector3 Right { get; init; }
            public Vector3 Up { get; init; }
            public float JitterX { get; init; }
            public float JitterY { get; init; }
            public float Fov { get; init; }
            public float Aspect { get; init; }
            public bool IsScoped { get; init; }
        }
    }
}
