using Cairo;
using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace StickRoast
{
    public class RoastingProgressRenderer : ModSystem, IRenderer
    {
        private const double ProgressArrowScale = 0.6;
        private const double ProgressArrowCanvasWidth = 130;
        private const double ProgressArrowCanvasHeight = 70;
        private const double ProgressArrowFillStart = 5;
        private const double ProgressArrowFillWidth = 125;
        private const double ProgressArrowGradientWidth = 200;
        private const double ProgressArrowGap = 6;
        private const double HotbarTopOffset = 70;

        private ICoreClientAPI? capi;
        private LoadedTexture? progressArrowOutline;
        private LoadedTexture? progressArrowFill;
        private float roastStartProgress;
        private float roastSecondsUsed;
        private float cookDuration;

        public double RenderOrder => 1.01;
        public int RenderRange => int.MaxValue;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Client;
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "stickroast-progress");
        }

        public override void Dispose()
        {
            if (capi != null)
            {
                capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
                capi = null;
            }

            progressArrowOutline?.Dispose();
            progressArrowFill?.Dispose();
            progressArrowOutline = null;
            progressArrowFill = null;
            End();
        }

        public void Begin(float progress, float duration)
        {
            roastStartProgress = progress;
            roastSecondsUsed = 0;
            cookDuration = duration;
        }

        public void Update(float secondsUsed)
        {
            roastSecondsUsed = secondsUsed;
        }

        public void End()
        {
            roastStartProgress = 0;
            roastSecondsUsed = 0;
            cookDuration = 0;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            ICoreClientAPI? api = capi;
            if (stage != EnumRenderStage.Ortho || api == null || api.HideGuis || cookDuration <= 0) return;

            (LoadedTexture outline, LoadedTexture fill) = GetProgressArrowTextures(api);
            float progress = GameMath.Clamp((roastStartProgress + roastSecondsUsed) / cookDuration, 0f, 1f);

            float x = (api.Render.FrameWidth - outline.Width) / 2f;
            float y = api.Render.FrameHeight
                - (float)GuiElement.scaled(HotbarTopOffset)
                - outline.Height
                - (float)GuiElement.scaled(ProgressArrowGap);

            api.Render.Render2DTexturePremultipliedAlpha(outline.TextureId, x, y, outline.Width, outline.Height);

            int fillWidth = (int)Math.Ceiling(GuiElement.scaled(ProgressArrowFillWidth * ProgressArrowScale) * progress);
            if (fillWidth <= 0) return;

            int fillX = (int)Math.Round(x + GuiElement.scaled(ProgressArrowFillStart * ProgressArrowScale));
            int fillY = api.Render.FrameHeight - (int)Math.Ceiling(y + outline.Height);

            api.Render.GlScissor(fillX, fillY, fillWidth, outline.Height);
            api.Render.GlScissorFlag(true);
            api.Render.Render2DTexturePremultipliedAlpha(fill.TextureId, x, y, fill.Width, fill.Height);
            api.Render.GlScissorFlag(false);
        }

        private (LoadedTexture Outline, LoadedTexture Fill) GetProgressArrowTextures(ICoreClientAPI api)
        {
            int width = (int)Math.Ceiling(GuiElement.scaled(ProgressArrowCanvasWidth * ProgressArrowScale));
            int height = (int)Math.Ceiling(GuiElement.scaled(ProgressArrowCanvasHeight * ProgressArrowScale));

            LoadedTexture? outline = progressArrowOutline;
            LoadedTexture? fill = progressArrowFill;

            if (outline == null || fill == null || outline.Width != width || outline.Height != height)
            {
                outline?.Dispose();
                fill?.Dispose();

                outline = CreateProgressArrowTexture(api, width, height, false);
                fill = CreateProgressArrowTexture(api, width, height, true);
                progressArrowOutline = outline;
                progressArrowFill = fill;
            }

            return (outline, fill);
        }

        private static LoadedTexture CreateProgressArrowTexture(ICoreClientAPI api, int width, int height, bool filled)
        {
            using ImageSurface surface = new(Format.Argb32, width, height);
            using Context ctx = new(surface);

            ctx.SetSourceRGBA(0, 0, 0, 0);
            ctx.Paint();
            ctx.Antialias = Antialias.Best;

            Matrix matrix = ctx.Matrix;
            matrix.Scale(GuiElement.scaled(ProgressArrowScale), GuiElement.scaled(ProgressArrowScale));
            ctx.Matrix = matrix;

            if (filled)
            {
                using LinearGradient gradient = new(0, 0, ProgressArrowGradientWidth, 0);
                gradient.AddColorStop(0, new Color(0, 0.4, 0, 1));
                gradient.AddColorStop(1, new Color(0.2, 0.6, 0.2, 1));
                ctx.SetSource(gradient);
                api.Gui.Icons.DrawArrowRight(ctx, 0, false, false);
            }
            else
            {
                api.Gui.Icons.DrawArrowRight(ctx, 2);
            }

            LoadedTexture texture = new(api);
            api.Gui.LoadOrUpdateCairoTexture(surface, false, ref texture);
            return texture;
        }
    }
}
