/*
 * Lone EFT DMA Radar - Copyright (c) 2026 Lone DMA
 * Licensed under GNU AGPLv3. See https://www.gnu.org/licenses/agpl-3.0.html
 */
using ImGuiNET;
using LoneEftDmaRadar.Tarkov.Unity.Structures;
using LoneEftDmaRadar.Tarkov.World.Loot;
using LoneEftDmaRadar.Tarkov.World.Player;
using LoneEftDmaRadar.Tarkov.World.Player.Helpers;
using LoneEftDmaRadar.UI.Skia;
using Silk.NET.OpenGL;

namespace LoneEftDmaRadar.UI.Widgets
{
    /// <summary>
    /// Aimview 'widget' that renders Skia content to an FBO-backed texture for ImGui display.
    /// </summary>
    public static class AimviewWidget
    {
        // Constants
        public const float AimviewBaseStrokeSize = 1.33f;
        private const float LOOT_RENDER_DISTANCE = 10f;
        private const float CONTAINER_RENDER_DISTANCE = 10f;

        private enum StickPoint
        {
            Pelvis,
            Spine1,
            Spine2,
            Spine3,
            Neck,
            Head,
            LeftCollar,
            LeftElbow,
            LeftHand,
            RightCollar,
            RightElbow,
            RightHand,
            LeftKnee,
            LeftFoot,
            RightKnee,
            RightFoot,
            Count
        }

        private static readonly Bones[] StickFigureBones =
        [
            Bones.HumanPelvis,
            Bones.HumanSpine1,
            Bones.HumanSpine2,
            Bones.HumanSpine3,
            Bones.HumanNeck,
            Bones.HumanHead,
            Bones.HumanLCollarbone,
            Bones.HumanLForearm2,
            Bones.HumanLPalm,
            Bones.HumanRCollarbone,
            Bones.HumanRForearm2,
            Bones.HumanRPalm,
            Bones.HumanLThigh2,
            Bones.HumanLFoot,
            Bones.HumanRThigh2,
            Bones.HumanRFoot
        ];

        private sealed class StickFigureScreenCache
        {
            public readonly SKPoint[] Points = new SKPoint[(int)StickPoint.Count];
            public readonly bool[] Valid = new bool[(int)StickPoint.Count];
            public int Width;
            public int Height;
            public float Zoom;

            public void Reset(int width, int height, float zoom)
            {
                Array.Clear(Valid);
                Width = width;
                Height = height;
                Zoom = zoom;
            }
        }

        private static readonly ConditionalWeakTable<AbstractPlayer, StickFigureScreenCache> StickFigureCaches = new();

        private static GL _gl;
        private static GRContext _grContext;

        // OpenGL resources
        private static uint _fbo;
        private static uint _texture;
        private static uint _depthRbo;

        // Skia resources
        private static SKSurface _surface;
        private static GRBackendRenderTarget _renderTarget;

        private static int _currentWidth;
        private static int _currentHeight;

        // Flag to track if we need to render this frame
        private static int _pendingWidth;
        private static int _pendingHeight;

        // Aimview camera state
        private static Vector3 _forward, _right, _up, _camPos;

        /// <summary>
        /// Whether the Aimview panel is open.
        /// </summary>
        public static bool IsOpen
        {
            get => Program.Config.AimviewWidget.Enabled;
            set => Program.Config.AimviewWidget.Enabled = value;
        }

        // Data sources
        private static LocalPlayer LocalPlayer => Memory.LocalPlayer;
        private static IReadOnlyCollection<AbstractPlayer> AllPlayers => Memory.Players;
        private static IEnumerable<LootItem> FilteredLoot => Memory.Loot?.FilteredLoot;
        private static IEnumerable<StaticLootContainer> Containers => Memory.Loot?.StaticContainers;

        public static void Initialize(GL gl, GRContext grContext)
        {
            _gl = gl;
            _grContext = grContext;
        }

        /// <summary>
        /// Called from the Skia render phase (before ImGui) to render to the FBO.
        /// </summary>
        public static void Render()
        {
            if (_surface is null || _fbo == 0)
                return;

            int width = _pendingWidth;
            int height = _pendingHeight;

            // Apply global UI scale
            float scale = Program.Config.UI.RadarScale;
            int scaledWidth = (int)(width / scale);
            int scaledHeight = (int)(height / scale);

            // Bind our FBO for Skia rendering
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.Viewport(0, 0, (uint)width, (uint)height);

            // Explicitly clear the backbuffer to avoid blending against stale pixels.
            _gl.ClearColor(0f, 0f, 0f, 0f); // TRANSPARENT
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.StencilBufferBit | ClearBufferMask.DepthBufferBit);

            // Draw to Skia surface
            var canvas = _surface.Canvas;
            canvas.Save();
            try
            {
                canvas.Scale(scale, scale);

                if (Program.State == AppState.InRaid && LocalPlayer is LocalPlayer localPlayer)
                {
                    // Update camera matrix
                    UpdateMatrix(localPlayer);

                    // Draw loot
                    if (Program.Config.Loot.Enabled)
                    {
                        DrawLoot(canvas, localPlayer, scaledWidth, scaledHeight);
                        if (Program.Config.Containers.Enabled)
                            DrawContainers(canvas, localPlayer, scaledWidth, scaledHeight);
                    }

                    // Draw players
                    DrawPlayers(canvas, localPlayer, scaledWidth, scaledHeight);

                    // Draw crosshair
                    DrawCrosshair(canvas, scaledWidth, scaledHeight);
                }
            }
            catch (Exception ex)
            {
                Logging.WriteLine($"CRITICAL AIMVIEW PANEL RENDER ERROR: {ex}");
            }
            finally
            {
                canvas.Restore();
                // Flush Skia to the FBO
                canvas.Flush();
                _grContext.Flush();
            }
        }

        /// <summary>
        /// Draw the ImGui window (called during ImGui phase).
        /// </summary>
        public static void Draw()
        {
            if (!IsOpen)
                return;

            // Default size for first use - ImGui persists position/size to imgui.ini automatically
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(300, 300), ImGuiCond.FirstUseEver);

            bool isOpen = IsOpen;
            if (!ImGui.Begin("Aimview", ref isOpen, ImGuiWindowFlags.None))
            {
                IsOpen = isOpen;
                ImGui.End();
                return;
            }
            IsOpen = isOpen;

            float viewZoom = Math.Clamp(Program.Config.AimviewWidget.PlayerScale, 10f, 100f);
            if (ImGui.SliderFloat("View Zoom", ref viewZoom, 10f, 100f, "%.0f", ImGuiSliderFlags.Logarithmic))
                Program.Config.AimviewWidget.PlayerScale = viewZoom;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("10 = native game view, 100 = 10x screen zoom");

            float skeletonWidth = Math.Clamp(Program.Config.AimviewWidget.SkeletonStrokeWidth, 0.5f, 6f);
            if (ImGui.SliderFloat("Skeleton Width", ref skeletonWidth, 0.5f, 6f, "%.1f px"))
                Program.Config.AimviewWidget.SkeletonStrokeWidth = skeletonWidth;

            float crosshairWidth = Math.Clamp(Program.Config.AimviewWidget.CrosshairStrokeWidth, 0.5f, 6f);
            if (ImGui.SliderFloat("Crosshair Width", ref crosshairWidth, 0.5f, 6f, "%.1f px"))
                Program.Config.AimviewWidget.CrosshairStrokeWidth = crosshairWidth;

            // Get the size of the content region
            var avail = ImGui.GetContentRegionAvail();
            int width = Math.Max(64, (int)avail.X);
            int height = Math.Max(64, (int)avail.Y);

            // Recreate FBO/surface if size changed
            EnsureFbo(width, height);

            // Request render for next frame
            _pendingWidth = width;
            _pendingHeight = height;

            if (_texture != 0)
            {
                // Display texture in ImGui (flip Y because OpenGL textures are bottom-up)
                ImGui.Image((nint)_texture, new System.Numerics.Vector2(width, height),
                    new System.Numerics.Vector2(0, 1), new System.Numerics.Vector2(1, 0));
            }
            else
            {
                ImGui.Text("Surface not available");
            }

            ImGui.End();
        }

        #region Aimview Rendering

        private static void UpdateMatrix(LocalPlayer localPlayer)
        {
            float yaw = localPlayer.Rotation.X * (MathF.PI / 180f);   // horizontal
            float pitch = localPlayer.Rotation.Y * (MathF.PI / 180f); // vertical

            float cy = MathF.Cos(yaw);
            float sy = MathF.Sin(yaw);
            float cp = MathF.Cos(pitch);
            float sp = MathF.Sin(pitch);

            _forward = new Vector3(
                sy * cp,   // X
               -sp,        // Y (up/down tilt)
                cy * cp    // Z
            );
            _forward = Vector3.Normalize(_forward);

            _right = new Vector3(cy, 0f, -sy);
            _right = Vector3.Normalize(_right);

            _up = Vector3.Normalize(Vector3.Cross(_right, _forward));
            _up = -_up;

            _camPos = localPlayer.LookPosition;
        }

        private static void DrawPlayers(SKCanvas canvas, LocalPlayer localPlayer, int width, int height)
        {
            var players = AllPlayers?
                .Where(p => p.IsActive && p.IsAlive && p != localPlayer);

            if (players is null)
                return;

            float strokeWidth = Math.Clamp(Program.Config.AimviewWidget.SkeletonStrokeWidth, 0.5f, 6f);

            foreach (var player in players)
            {
                float distance = Vector3.Distance(localPlayer.LookPosition, player.Position);
                if (distance > Program.Config.UI.MaxDistance)
                    continue;

                var playerPaint = GetPaint(player);
                playerPaint.Style = SKPaintStyle.Stroke;
                playerPaint.StrokeWidth = strokeWidth;
                playerPaint.StrokeCap = SKStrokeCap.Round;
                playerPaint.StrokeJoin = SKStrokeJoin.Round;
                playerPaint.IsAntialias = true;
                DrawStickFigure(canvas, player, width, height, playerPaint);
            }
        }

        /// <summary>
        /// Draws the player's current DMA-backed skeleton pose.
        /// </summary>
        private static void DrawStickFigure(
            SKCanvas canvas,
            AbstractPlayer player,
            int width,
            int height,
            SKPaint playerPaint)
        {
            Span<SKPoint> points = stackalloc SKPoint[(int)StickPoint.Count];
            float zoom = GetViewZoom();
            var cache = StickFigureCaches.GetOrCreateValue(player);
            if (cache.Width != width || cache.Height != height || MathF.Abs(cache.Zoom - zoom) > 0.001f)
                cache.Reset(width, height, zoom);

            int anchorIndex = (int)StickPoint.Spine2;
            if (player.TryGetBonePosition(StickFigureBones[anchorIndex], out var anchorWorld) &&
                ProjectWorldPoint(in anchorWorld, width, height, out var anchorPoint))
            {
                cache.Points[anchorIndex] = anchorPoint;
                cache.Valid[anchorIndex] = true;
            }
            else if (!cache.Valid[anchorIndex])
            {
                return;
            }

            points[anchorIndex] = cache.Points[anchorIndex];

            for (int i = 0; i < StickFigureBones.Length; i++)
            {
                if (i == anchorIndex)
                    continue;

                if (player.TryGetBonePosition(StickFigureBones[i], out var boneWorld) &&
                    ProjectWorldPoint(in boneWorld, width, height, out var point))
                {
                    cache.Points[i] = point;
                    cache.Valid[i] = true;
                }

                points[i] = cache.Valid[i] ? cache.Points[i] : points[anchorIndex];
            }

            var head = points[(int)StickPoint.Head];
            var neck = points[(int)StickPoint.Neck];
            float headNeckDistance = MathF.Sqrt(
                MathF.Pow(head.X - neck.X, 2f) + MathF.Pow(head.Y - neck.Y, 2f));
            float headRadius = Math.Clamp(headNeckDistance * 0.45f, 2f, 24f);

            DrawStickFigureLayer(canvas, points, headRadius, playerPaint);
        }

        private static void DrawStickFigureLayer(
            SKCanvas canvas,
            ReadOnlySpan<SKPoint> points,
            float headRadius,
            SKPaint paint)
        {
            DrawBone(points, StickPoint.Pelvis, StickPoint.Spine1, canvas, paint);
            DrawBone(points, StickPoint.Spine1, StickPoint.Spine2, canvas, paint);
            DrawBone(points, StickPoint.Spine2, StickPoint.Spine3, canvas, paint);
            DrawBone(points, StickPoint.Spine3, StickPoint.Neck, canvas, paint);
            DrawBone(points, StickPoint.Neck, StickPoint.Head, canvas, paint);

            DrawBone(points, StickPoint.LeftCollar, StickPoint.LeftElbow, canvas, paint);
            DrawBone(points, StickPoint.LeftElbow, StickPoint.LeftHand, canvas, paint);
            DrawBone(points, StickPoint.RightCollar, StickPoint.RightElbow, canvas, paint);
            DrawBone(points, StickPoint.RightElbow, StickPoint.RightHand, canvas, paint);

            DrawBone(points, StickPoint.Pelvis, StickPoint.LeftKnee, canvas, paint);
            DrawBone(points, StickPoint.LeftKnee, StickPoint.LeftFoot, canvas, paint);
            DrawBone(points, StickPoint.Pelvis, StickPoint.RightKnee, canvas, paint);
            DrawBone(points, StickPoint.RightKnee, StickPoint.RightFoot, canvas, paint);

            canvas.DrawCircle(points[(int)StickPoint.Head], headRadius, paint);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DrawBone(
            ReadOnlySpan<SKPoint> points,
            StickPoint start,
            StickPoint end,
            SKCanvas canvas,
            SKPaint paint)
        {
            canvas.DrawLine(points[(int)start], points[(int)end], paint);
        }

        private static void DrawLoot(SKCanvas canvas, LocalPlayer localPlayer, int width, int height)
        {
            if (FilteredLoot is not IEnumerable<LootItem> loot)
                return;

            const float boxHalf = 4f;

            foreach (var item in loot)
            {
                var itemPos = item.Position;
                var dist = Vector3.Distance(localPlayer.LookPosition, itemPos);
                if (dist > LOOT_RENDER_DISTANCE)
                    continue;

                if (!WorldToScreen(in itemPos, width, height, out var scrPos))
                    continue;

                DrawBoxAndLabel(canvas, scrPos, boxHalf, $"{item.GetUILabel()} ({dist:n1}m)",
                    SKPaints.PaintAimviewWidgetLoot, SKPaints.TextAimviewWidgetLoot);
            }
        }

        private static void DrawContainers(SKCanvas canvas, LocalPlayer localPlayer, int width, int height)
        {
            if (Containers is not IEnumerable<StaticLootContainer> containers)
                return;

            const float boxHalf = 4f;

            foreach (var container in containers)
            {
                if (!Program.Config.Containers.Selected.ContainsKey(container.ID ?? "NULL"))
                    continue;

                var cPos = container.Position;
                var dist = Vector3.Distance(localPlayer.LookPosition, cPos);
                if (dist > CONTAINER_RENDER_DISTANCE)
                    continue;

                if (!WorldToScreen(in cPos, width, height, out var scrPos))
                    continue;

                DrawBoxAndLabel(canvas, scrPos, boxHalf, $"{container.Name} ({dist:n1}m)",
                    SKPaints.PaintAimviewWidgetLoot, SKPaints.TextAimviewWidgetLoot);
            }
        }

        private static void DrawBoxAndLabel(SKCanvas canvas, SKPoint center, float half, string label, SKPaint boxPaint, SKPaint textPaint)
        {
            var rect = new SKRect(center.X - half, center.Y - half, center.X + half, center.Y + half);
            var textPt = new SKPoint(center.X, center.Y + 12.5f);

            canvas.DrawRect(rect, boxPaint);
            canvas.DrawText(label, textPt, SKTextAlign.Left, SKFonts.AimviewWidgetFont, textPaint);
        }

        private static void DrawCrosshair(SKCanvas canvas, int width, int height)
        {
            float centerX = width / 2f;
            float centerY = height / 2f;
            var paint = SKPaints.PaintAimviewWidgetCrosshair;
            paint.StrokeWidth = Math.Clamp(Program.Config.AimviewWidget.CrosshairStrokeWidth, 0.5f, 6f);
            paint.IsAntialias = true;

            canvas.DrawLine(0, centerY, width, centerY, paint);
            canvas.DrawLine(centerX, 0, centerX, height, paint);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKPaint GetPaint(AbstractPlayer player)
        {
            if (player.IsFocused)
                return SKPaints.PaintAimviewWidgetFocused;
            if (player is LocalPlayer)
                return SKPaints.PaintAimviewWidgetLocalPlayer;

            return player.Type switch
            {
                PlayerType.Teammate => SKPaints.PaintAimviewWidgetTeammate,
                PlayerType.PMC => SKPaints.PaintAimviewWidgetPMC,
                PlayerType.AIScav => SKPaints.PaintAimviewWidgetScav,
                PlayerType.AIRaider => SKPaints.PaintAimviewWidgetRaider,
                PlayerType.AIBoss => SKPaints.PaintAimviewWidgetBoss,
                PlayerType.PScav => SKPaints.PaintAimviewWidgetPScav,
                _ => SKPaints.PaintAimviewWidgetPMC
            };
        }

        private static bool WorldToScreen(in Vector3 world, int width, int height, out SKPoint scr)
        {
            if (!ProjectWorldPoint(in world, width, height, out scr))
                return false;

            return !(scr.X < 0 || scr.X > width || scr.Y < 0 || scr.Y > height);
        }

        private static bool ProjectWorldPoint(in Vector3 world, int width, int height, out SKPoint scr)
        {
            scr = default;

            var dir = world - _camPos;

            float dz = Vector3.Dot(dir, _forward);
            if (dz <= 0f)
                return false;

            float dx = Vector3.Dot(dir, _right);
            float dy = Vector3.Dot(dir, _up);

            // Perspective divide
            float nx = dx / dz;
            float ny = dy / dz;

            const float PSEUDO_FOV = 1.0f;
            nx /= PSEUDO_FOV;
            ny /= PSEUDO_FOV;

            scr.X = width * 0.5f + nx * (width * 0.5f);
            scr.Y = height * 0.5f - ny * (height * 0.5f);

            ApplyViewZoom(ref scr, width, height);

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetViewZoom() =>
            Math.Clamp(Program.Config.AimviewWidget.PlayerScale, 10f, 100f) / 10f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyViewZoom(ref SKPoint point, int width, int height)
        {
            float zoom = GetViewZoom();
            float centerX = width * 0.5f;
            float centerY = height * 0.5f;
            point.X = centerX + (point.X - centerX) * zoom;
            point.Y = centerY + (point.Y - centerY) * zoom;
        }

        #endregion

        #region FBO Management

        private static void EnsureFbo(int width, int height)
        {
            if (_fbo != 0 && _currentWidth == width && _currentHeight == height)
                return;

            _currentWidth = width;
            _currentHeight = height;

            // Dispose old resources
            DestroyFbo();

            // Create texture
            _texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            unsafe
            {
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba8,
                    (uint)width,
                    (uint)height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    null);
            }
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

            // Create depth renderbuffer (required for some Skia operations)
            _depthRbo = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRbo);
            _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);

            // Create FBO
            _fbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _texture, 0);
            _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthRbo);

            // Check FBO status
            var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
            {
                Logging.WriteLine($"AimviewPanel: FBO incomplete: {status}");
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                DestroyFbo();
                return;
            }

            // Unbind FBO
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

            // Create Skia surface targeting the FBO
            var fbInfo = new GRGlFramebufferInfo(_fbo, (uint)InternalFormat.Rgba8);
            _renderTarget = new GRBackendRenderTarget(width, height, 0, 8, fbInfo);
            _surface = SKSurface.Create(_grContext, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);

            if (_surface is null)
            {
                Logging.WriteLine("AimviewPanel: Failed to create Skia surface");
                DestroyFbo();
            }
        }

        private static void DestroyFbo()
        {
            _surface?.Dispose();
            _surface = null;

            _renderTarget?.Dispose();
            _renderTarget = null;

            if (_fbo != 0)
            {
                _gl.DeleteFramebuffer(_fbo);
                _fbo = 0;
            }

            if (_texture != 0)
            {
                _gl.DeleteTexture(_texture);
                _texture = 0;
            }

            if (_depthRbo != 0)
            {
                _gl.DeleteRenderbuffer(_depthRbo);
                _depthRbo = 0;
            }
        }

        #endregion
    }
}

