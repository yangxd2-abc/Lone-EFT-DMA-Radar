/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 */
using LoneEftDmaRadar.Misc;
using System.Buffers.Binary;
using VmmSharpEx.Extensions;

namespace LoneEftDmaRadar.Tarkov.Unity.Structures
{
    public sealed class UnityTransform
    {
        private const int MAX_ITERATIONS = 4000;
        private const int MAX_TRANSFORM_INDEX = 128000;
        private const int MAX_SCANNED_TRANSFORM_INDEX = 16384;
        private const uint MAX_LAYOUT_SCAN_OFFSET = 0x100;
        private static readonly object _layoutSync = new();
        private static TransformLayout _layout = new(
            UnityOffsets.TransformAccess_IndexOffset,
            UnityOffsets.TransformAccess_HierarchyOffset,
            UnityOffsets.Hierarchy_VerticesOffset,
            UnityOffsets.Hierarchy_IndicesOffset);
        private readonly bool _useCache;
        private readonly int _index;
        private readonly ulong _hierarchyAddr;
        private readonly ReadOnlyMemory<int> _indices;

        private Vector3 _position;
        /// <summary>
        /// Unity World Position for this Transform.
        /// </summary>
        public ref readonly Vector3 Position => ref _position;

        public UnityTransform(ulong transformInternal, bool useCache = false)
        {
            /// Constructor
            TransformInternal = transformInternal;
            _useCache = useCache;

            var resolved = ResolveLayout(transformInternal, useCache);
            _index = resolved.Index;
            _hierarchyAddr = resolved.Hierarchy;
            IndicesAddr = resolved.Indices;
            VerticesAddr = resolved.Vertices;
            /// Populate Indices once for the Life of the Transform.
            _indices = ReadIndices();
        }

        /// <summary>
        /// Runtime-resolved native Unity Transform layout. These can change independently
        /// of the managed IL2CPP field offsets after a Unity/game update.
        /// </summary>
        public static uint TransformAccessHierarchyOffset => _layout.HierarchyOffset;
        public static uint HierarchyVerticesOffset => _layout.VerticesOffset;

        private static ResolvedTransform ResolveLayout(ulong transformInternal, bool useCache)
        {
            var layout = _layout;
            if (TryReadTransform(transformInternal, layout, useCache, MAX_TRANSFORM_INDEX, out var resolved))
                return resolved;

            lock (_layoutSync)
            {
                layout = _layout;
                if (TryReadTransform(transformInternal, layout, false, MAX_TRANSFORM_INDEX, out resolved))
                    return resolved;

                if (!TryScanLayout(transformInternal, out layout, out resolved))
                {
                    throw new InvalidOperationException(
                        $"Unable to resolve native Unity Transform layout for 0x{transformInternal:X}. " +
                        $"Fallback offsets: hierarchy=0x{_layout.HierarchyOffset:X}, index=0x{_layout.IndexOffset:X}, " +
                        $"vertices=0x{_layout.VerticesOffset:X}, indices=0x{_layout.IndicesOffset:X}.");
                }

                _layout = layout;
                Logging.WriteLine(
                    $"[UnityTransform] Native layout resolved: " +
                    $"hierarchy=0x{layout.HierarchyOffset:X}, index=0x{layout.IndexOffset:X}, " +
                    $"vertices=0x{layout.VerticesOffset:X}, indices=0x{layout.IndicesOffset:X}");
                return resolved;
            }
        }

        private static bool TryScanLayout(
            ulong transformInternal,
            out TransformLayout layout,
            out ResolvedTransform resolved)
        {
            layout = default;
            resolved = default;

            Span<byte> access = stackalloc byte[(int)MAX_LAYOUT_SCAN_OFFSET + sizeof(ulong)];
            try
            {
                Memory.ReadSpan(transformInternal, access, false);
            }
            catch
            {
                return false;
            }

            // Unity normally stores the hierarchy pointer immediately before its index.
            // Prefer that shape, then fall back to independent offsets for layout changes.
            for (uint hierarchyOffset = 0; hierarchyOffset <= MAX_LAYOUT_SCAN_OFFSET; hierarchyOffset += 8)
            {
                ulong hierarchy = ReadUInt64(access, hierarchyOffset);
                if (!hierarchy.IsValidUserVA())
                    continue;

                uint indexOffset = hierarchyOffset + 8;
                if (indexOffset <= MAX_LAYOUT_SCAN_OFFSET &&
                    TryResolveHierarchy(transformInternal, hierarchy, indexOffset, access, out layout, out resolved))
                {
                    return true;
                }
            }

            for (uint hierarchyOffset = 0; hierarchyOffset <= MAX_LAYOUT_SCAN_OFFSET; hierarchyOffset += 8)
            {
                ulong hierarchy = ReadUInt64(access, hierarchyOffset);
                if (!hierarchy.IsValidUserVA())
                    continue;

                for (uint indexOffset = 0; indexOffset <= MAX_LAYOUT_SCAN_OFFSET; indexOffset += 4)
                {
                    if (TryResolveHierarchy(transformInternal, hierarchy, indexOffset, access, out layout, out resolved))
                        return true;
                }
            }

            return false;
        }

        private static bool TryResolveHierarchy(
            ulong transformInternal,
            ulong hierarchy,
            uint indexOffset,
            ReadOnlySpan<byte> access,
            out TransformLayout layout,
            out ResolvedTransform resolved)
        {
            layout = default;
            resolved = default;
            int index = ReadInt32(access, indexOffset);
            if ((uint)index > MAX_SCANNED_TRANSFORM_INDEX)
                return false;

            Span<byte> hierarchyData = stackalloc byte[(int)MAX_LAYOUT_SCAN_OFFSET + sizeof(ulong)];
            try
            {
                Memory.ReadSpan(hierarchy, hierarchyData, false);
            }
            catch
            {
                return false;
            }

            for (uint verticesOffset = 0; verticesOffset <= MAX_LAYOUT_SCAN_OFFSET; verticesOffset += 8)
            {
                ulong vertices = ReadUInt64(hierarchyData, verticesOffset);
                if (!vertices.IsValidUserVA())
                    continue;

                for (uint indicesOffset = 0; indicesOffset <= MAX_LAYOUT_SCAN_OFFSET; indicesOffset += 8)
                {
                    if (indicesOffset == verticesOffset)
                        continue;

                    ulong indices = ReadUInt64(hierarchyData, indicesOffset);
                    if (!indices.IsValidUserVA() || !ValidateTransformData(index, vertices, indices))
                        continue;

                    uint hierarchyOffset = FindPointerOffset(access, hierarchy);
                    layout = new(indexOffset, hierarchyOffset, verticesOffset, indicesOffset);
                    resolved = new(index, hierarchy, vertices, indices);
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadTransform(
            ulong transformInternal,
            TransformLayout layout,
            bool useCache,
            int maxIndex,
            out ResolvedTransform resolved)
        {
            resolved = default;
            try
            {
                int index = Memory.ReadValue<int>(transformInternal + layout.IndexOffset, useCache);
                if ((uint)index > (uint)maxIndex)
                    return false;

                ulong hierarchy = Memory.ReadValue<ulong>(transformInternal + layout.HierarchyOffset, useCache);
                if (!hierarchy.IsValidUserVA())
                    return false;

                ulong vertices = Memory.ReadValue<ulong>(hierarchy + layout.VerticesOffset, useCache);
                ulong indices = Memory.ReadValue<ulong>(hierarchy + layout.IndicesOffset, useCache);
                if (!vertices.IsValidUserVA() || !indices.IsValidUserVA() ||
                    !ValidateTransformData(index, vertices, indices, useCache))
                {
                    return false;
                }

                resolved = new(index, hierarchy, vertices, indices);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ValidateTransformData(int index, ulong vertices, ulong indices, bool useCache = false)
        {
            try
            {
                int rootParent = Memory.ReadValue<int>(indices, useCache);
                int parent = Memory.ReadValue<int>(indices + (uint)index * sizeof(int), useCache);
                if (rootParent != -1 || parent < -1 || parent >= index)
                    return false;

                var root = Memory.ReadValue<TrsX>(vertices, useCache);
                var value = Memory.ReadValue<TrsX>(vertices + (uint)index * (uint)Unsafe.SizeOf<TrsX>(), useCache);
                return IsReasonableTrs(root) && IsReasonableTrs(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsReasonable(Vector3 value, float max) =>
            value.IsNormalOrZero() &&
            MathF.Abs(value.X) <= max && MathF.Abs(value.Y) <= max && MathF.Abs(value.Z) <= max;

        private static bool IsReasonableTrs(TrsX value)
        {
            float rotationLengthSquared = value.q.LengthSquared();
            float scaleLengthSquared = value.s.LengthSquared();
            return IsReasonable(value.t, 10_000_000f) &&
                IsReasonable(value.s, 100_000f) &&
                rotationLengthSquared is >= 0.25f and <= 2.25f &&
                scaleLengthSquared is > 0.000001f;
        }

        private static ulong ReadUInt64(ReadOnlySpan<byte> data, uint offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(data.Slice((int)offset, sizeof(ulong)));

        private static int ReadInt32(ReadOnlySpan<byte> data, uint offset) =>
            BinaryPrimitives.ReadInt32LittleEndian(data.Slice((int)offset, sizeof(int)));

        private static uint FindPointerOffset(ReadOnlySpan<byte> data, ulong value)
        {
            for (uint offset = 0; offset <= MAX_LAYOUT_SCAN_OFFSET; offset += 8)
            {
                if (ReadUInt64(data, offset) == value)
                    return offset;
            }
            return uint.MaxValue;
        }

        private ReadOnlySpan<int> Indices
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _indices.Span;
        }
        /// <summary>
        /// TransformInternal address for this Transform.
        /// </summary>
        public ulong TransformInternal { get; }
        /// <summary>
        /// Indices address for this Transform.
        /// </summary>
        private ulong IndicesAddr { get; }
        /// <summary>
        /// Vertices address for this Transform.
        /// </summary>
        public ulong VerticesAddr { get; }
        /// <summary>
        /// The number of elements in the indices/vertices arrays for this Transform.
        /// </summary>
        public int Count => _index + 1;

        #region Transform Methods

        /// <summary>
        /// Update Transform's World Position.
        /// </summary>
        /// <returns>Ref to World Position</returns>
        public ref Vector3 UpdatePosition(Span<TrsX> vertices = default)
        {
            IMemoryOwner<TrsX> standaloneVertices = null;
            try
            {
                if (vertices.IsEmpty)
                {
                    standaloneVertices = ReadVertices();
                    vertices = standaloneVertices.Memory.Span;
                }

                ArgumentOutOfRangeException.ThrowIfLessThan(vertices.Length, Count, nameof(vertices));
                var worldPos = vertices[_index].t;
                int index = Indices[_index];
                int iterations = 0;
                while (index >= 0)
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations++, MAX_ITERATIONS, nameof(iterations));
                    ThrowIfInvalidParentIndex(index, vertices.Length);
                    var parent = vertices[index];

                    worldPos = parent.q.Multiply(worldPos);
                    worldPos *= parent.s;
                    worldPos += parent.t;

                    index = Indices[index];
                }

                worldPos.ThrowIfAbnormalAndNotZero(nameof(worldPos));
                _position = worldPos;
                return ref _position;
            }
            finally
            {
                standaloneVertices?.Dispose();
            }
        }

        private void ThrowIfInvalidParentIndex(int index, int verticesLength)
        {
            if ((uint)index >= (uint)verticesLength || (uint)index >= (uint)_indices.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index),
                    $"Invalid transform parent index {index} for transform count {Count}.");
            }
        }

        /// <summary>
        /// Get Transform's World Rotation.
        /// </summary>
        /// <returns>World Rotation</returns>
        public Quaternion GetRotation(Span<TrsX> vertices = default)
        {
            IMemoryOwner<TrsX> standaloneVertices = null;
            try
            {
                if (vertices.IsEmpty)
                {
                    standaloneVertices = ReadVertices();
                    vertices = standaloneVertices.Memory.Span;
                }

                var worldRot = vertices[_index].q;
                int index = Indices[_index];
                int iterations = 0;
                while (index >= 0)
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations++, MAX_ITERATIONS, nameof(iterations));
                    var parent = vertices[index];

                    worldRot = parent.q * worldRot;

                    index = Indices[index];
                }

                worldRot.ThrowIfAbnormal(nameof(worldRot));
                return worldRot;
            }
            finally
            {
                standaloneVertices?.Dispose();
            }
        }

        /// <summary>
        /// Get Transform's Root World Position.
        /// </summary>
        /// <returns>Root World Position</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetRootPosition()
        {
            throw new NotImplementedException();
            //Vector3 rootPos = Memory.ReadValue<TrsX>(_hierarchyAddr + UnityOffsets.Hierarchy_RootPositionOffset, _useCache).t;
            //rootPos.ThrowIfAbnormal(nameof(rootPos));
            //return rootPos;
        }

        /// <summary>
        /// Get Transform's Local Position.
        /// </summary>
        /// <returns>Local Position</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetLocalPosition()
        {
            return Memory.ReadValue<TrsX>(VerticesAddr + (uint)_index * (uint)Unsafe.SizeOf<TrsX>(), _useCache).t;
        }

        /// <summary>
        /// Get Transform's Local Scale.
        /// </summary>
        /// <returns>Local Scale</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetLocalScale()
        {
            return Memory.ReadValue<TrsX>(VerticesAddr + (uint)_index * (uint)Unsafe.SizeOf<TrsX>(), _useCache).s;
        }
        /// <summary>
        /// Get Transform's Local Rotation.
        /// </summary>
        /// <returns>Local Rotation</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion GetLocalRotation()
        {
            return Memory.ReadValue<TrsX>(VerticesAddr + (uint)_index * (uint)Unsafe.SizeOf<TrsX>(), _useCache).q;
        }


        /// <summary>
        /// Convert from Local Point to World Point.
        /// </summary>
        /// <param name="localPoint">Local Point</param>
        /// <returns>World Point.</returns>
        public Vector3 TransformPoint(Vector3 localPoint, Span<TrsX> vertices = default)
        {
            IMemoryOwner<TrsX> standaloneVertices = null;
            try
            {
                if (vertices.IsEmpty)
                {
                    standaloneVertices = ReadVertices();
                    vertices = standaloneVertices.Memory.Span;
                }

                var worldPos = localPoint;
                int index = _index;
                int iterations = 0;
                while (index >= 0)
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations++, MAX_ITERATIONS, nameof(iterations));
                    var parent = vertices[index];

                    worldPos *= parent.s;
                    worldPos = parent.q.Multiply(worldPos);
                    worldPos += parent.t;

                    index = Indices[index];
                }

                worldPos.ThrowIfAbnormalAndNotZero(nameof(worldPos));
                return worldPos;
            }
            finally
            {
                standaloneVertices?.Dispose();
            }
        }

        /// <summary>
        /// Convert from World Point to Local Point.
        /// </summary>
        /// <param name="worldPoint">World Point</param>
        /// <returns>Local Point</returns>
        public Vector3 InverseTransformPoint(Vector3 worldPoint, Span<TrsX> vertices = default)
        {
            IMemoryOwner<TrsX> standaloneVertices = null;
            try
            {
                if (vertices.IsEmpty)
                {
                    standaloneVertices = ReadVertices();
                    vertices = standaloneVertices.Memory.Span;
                }

                var worldPos = vertices[_index].t;
                var worldRot = vertices[_index].q;

                Vector3 localScale = vertices[_index].s;

                int index = Indices[_index];
                int iterations = 0;
                while (index >= 0)
                {
                    ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations++, MAX_ITERATIONS, nameof(iterations));
                    var parent = vertices[index];

                    worldPos = parent.q.Multiply(worldPos);
                    worldPos *= parent.s;
                    worldPos += parent.t;

                    worldRot = parent.q * worldRot;

                    index = Indices[index];
                }

                var local = Quaternion.Conjugate(worldRot).Multiply(worldPoint - worldPos);
                return local / localScale;
            }
            finally
            {
                standaloneVertices?.Dispose();
            }
        }
        #endregion

        #region Structures
        private readonly record struct TransformLayout(
            uint IndexOffset,
            uint HierarchyOffset,
            uint VerticesOffset,
            uint IndicesOffset);

        private readonly record struct ResolvedTransform(
            int Index,
            ulong Hierarchy,
            ulong Vertices,
            ulong Indices);

        [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 48)]
        public readonly struct TrsX
        {
            [FieldOffset(0x0)]
            public readonly Vector3 t;
            // pad 0x4
            [FieldOffset(0x10)]
            public readonly Quaternion q;
            [FieldOffset(0x20)]
            public readonly Vector3 s;
            // pad 0x4
        }
        #endregion

        #region ReadMem
        /// <summary>
        /// Read Indices for this Transform.
        /// NOTE: Indices does not need to be updated for the life of the transform.
        /// </summary>
        private int[] ReadIndices()
        {
            var indices = new int[Count];
            Memory.ReadSpan(IndicesAddr, indices.AsSpan(), _useCache);
            return indices;
        }

        /// <summary>
        /// Read Updated Vertices for this Transform.
        /// </summary>
        public IMemoryOwner<TrsX> ReadVertices()
        {
            return Memory.ReadPooled<TrsX>(VerticesAddr, Count, _useCache);
        }
        #endregion

    }

    public static class UnityTransformExtensions
    {
        private static readonly Vector3 _left = new Vector3(-1, 0, 0);
        private static readonly Vector3 _right = new(1, 0, 0);
        private static readonly Vector3 _up = new(0, 1, 0);
        private static readonly Vector3 _down = new(0, -1, 0);
        private static readonly Vector3 _forward = new(0, 0, 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Left(this Quaternion q) =>
            q.Multiply(_left);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Right(this Quaternion q) =>
            q.Multiply(_right);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Up(this Quaternion q) =>
            q.Multiply(_up);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Down(this Quaternion q) =>
            q.Multiply(_down);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Forward(this Quaternion q) =>
            q.Multiply(_forward);

        /// <summary>
        /// Convert Local Direction to World Direction.
        /// </summary>
        /// <param name="localDirection">Local Direction.</param>
        /// <returns>World Direction.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 TransformDirection(this Quaternion q, Vector3 localDirection)
        {
            return q.Multiply(localDirection);
        }

        /// <summary>
        /// Convert World Direction to Local Direction.
        /// </summary>
        /// <param name="worldDirection">World Direction.</param>
        /// <returns>Local Direction.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 InverseTransformDirection(this Quaternion q, Vector3 worldDirection)
        {
            return Quaternion.Conjugate(q).Multiply(worldDirection);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Multiply(this Quaternion q, Vector3 vector)
        {
            var m = Matrix4x4.CreateFromQuaternion(q);
            return Vector3.Transform(vector, m);
        }
    }
}
