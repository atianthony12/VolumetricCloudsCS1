using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// SMOOTHER CLOUDS (1.5.0, <see cref="Settings.CloudTemporal"/>, off by default): the clouds marched at
    /// half the screen's size each way (since 2026-11-17 the row "Cloud drawing size" can make it a third or a
    /// quarter: <see cref="SmootherSize"/>), every frame, and blended with the frames before -- temporal
    /// accumulation, as Unity HDRP, Horizon Zero Dawn and KSP's EVE-Redux draw their clouds. A quarter of
    /// the marches per frame, and the march's jitter moves on every frame, so its grain averages away.
    /// </summary>
    /// <remarks>
    /// Each frame, as the main camera is about to draw the cloud box (its OnWillRenderObject, where the
    /// camera already stands where it draws from), one command buffer is run at once:
    ///   1. the cloud material's own pass in mode 1, over a quad into <see cref="_march"/>: the clouds
    ///      alone, through the whole slab, before the distance fade, the camera's rays shifted inside
    ///      their texel by a different amount every frame (the R2 sequence), the jitter by the golden ratio;
    ///   2. the same shader in mode 2 (<see cref="_resolve"/>) into the other history target: this march
    ///      blended with the last blend, which is moved to where the same clouds were seen last frame (the
    ///      camera's move and the wind's) and held inside what this march has round the texel, so a moved
    ///      or changed cloud leaves no ghost.
    /// The camera's draw then reads the blend instead of marching (mode 0, _TemporalOn) wherever that is
    /// the same picture: where a building or a hill stands inside the slab, or a bolt shows, those pixels
    /// march as before. Other cameras always march.
    ///
    /// Probed in the 5.6 editor (2026-10-12, D3D11): OnWillRenderObject and a command buffer run inside
    /// it, a quad drawn with a MaterialPropertyBlock into a half-size ARGBHalf target, its up taken from
    /// GL.GetGPUProjectionMatrix (-1 there), read back by the camera's own draw through the matrix with an
    /// error of 0.0003 (0.36 with the other up), and the scene drawn exactly as without it.
    ///
    /// Off: nothing runs and the targets are released (invariant 10).
    /// </remarks>
    public sealed class CloudTemporal
    {
        /// <summary>How much of the frames before each frame keeps: about ten frames' average.</summary>
        public const float Keep = 0.9f;

        /// <summary>Which blend was last logged (the test row SmootherCloudsSharp); null: none yet.</summary>
        private bool? _loggedSharp;

        /// <summary>While lightning flashes: the flash shows within a frame or two instead of fading in.</summary>
        public const float KeepInFlash = 0.5f;

        /// <summary>A camera that moves more than this in one frame starts afresh (a jump, not a pan).</summary>
        private const float CutDistance = 1000f;

        /// <summary>The same for a turn, in degrees.</summary>
        private const float CutAngle = 45f;

        private static readonly int IdTemporalMode = Shader.PropertyToID("_TemporalMode");
        private static readonly int IdTemporalTex = Shader.PropertyToID("_TemporalTex");
        private static readonly int IdTemporalVP = Shader.PropertyToID("_TemporalVP");
        private static readonly int IdCorner0 = Shader.PropertyToID("_TemporalCorner0");
        private static readonly int IdCorner1 = Shader.PropertyToID("_TemporalCorner1");
        private static readonly int IdCorner2 = Shader.PropertyToID("_TemporalCorner2");
        private static readonly int IdCorner3 = Shader.PropertyToID("_TemporalCorner3");
        private static readonly int IdTemporalEye = Shader.PropertyToID("_TemporalEye");
        private static readonly int IdTemporalSize = Shader.PropertyToID("_TemporalSize");
        private static readonly int IdTemporalJitter = Shader.PropertyToID("_TemporalJitter");
        private static readonly int IdBowTex = Shader.PropertyToID("_BowTex");
        private static readonly int IdTemporalPrevVP = Shader.PropertyToID("_TemporalPrevVP");
        private static readonly int IdTemporalParams = Shader.PropertyToID("_TemporalParams");
        private static readonly int IdTemporalUp = Shader.PropertyToID("_TemporalUp");
        private static readonly int IdTemporalUp2 = Shader.PropertyToID("_TemporalUp2");
        private static readonly int IdCloudBottom = Shader.PropertyToID("_CloudBottom");
        private static readonly int IdCloudTop = Shader.PropertyToID("_CloudTop");
        private static readonly int IdMaxDistance = Shader.PropertyToID("_MaxDistance");
        private static readonly int IdWeatherPhase = Shader.PropertyToID("_WeatherPhase");
        private static readonly int IdWeatherTile = Shader.PropertyToID("_WeatherTile");
        private static readonly int IdFlashCount = Shader.PropertyToID("_FlashCount");

        private readonly Material _resolve;
        private readonly Mesh _quad;
        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        private readonly RenderTexture[] _history = new RenderTexture[2];
        private readonly Vector4[] _corners = new Vector4[4];

        private CommandBuffer _buffer;
        private RenderTexture _march;
        private int _current;
        private bool _hasHistory;
        private bool _failed;
        private float _up = 1f;
        private int _lastFrame = -1;
        private long _marches;

        /// <summary>Draws in a row at a size the targets don't have (see Render).</summary>
        private int _otherSize;

        /// <summary>The size row's choice the targets were made for (-1: none yet).</summary>
        private int _madeChoice = -1;

        private Matrix4x4 _previousVP;
        private Vector3 _previousEye;
        private Vector3 _previousForward;
        private Vector2 _previousPhase;

        public CloudTemporal(Shader shader)
        {
            _resolve = new Material(shader) { name = "VolumetricCloudsTemporalBlend" };
            _resolve.SetFloat(IdTemporalMode, 2f);
            // The blend marches nothing: never the extras' variant (CloudShaders.PlusKeyword), whatever the clouds draw.
            _resolve.DisableKeyword(CloudShaders.PlusKeyword);

            _quad = new Mesh { name = "VolumetricCloudsTemporalQuad" };
            _quad.vertices = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(1f, 1f, 0f),
            };
            _quad.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        /// <summary>The targets exist: smoother clouds are being drawn.</summary>
        public bool Running
        {
            get { return _march != null; }
        }

        /// <summary>Starts afresh since the last <see cref="TakeCounts"/> (detailed log).</summary>
        private int _resets;

        /// <summary>Frames blended since the last <see cref="TakeCounts"/> (detailed log).</summary>
        private int _blended;

        /// <summary>For the detailed frame line: frames blended and fresh starts since the last call.</summary>
        public string TakeCounts()
        {
            string counts = _blended + " blended, " + _resets + " fresh starts";
            _blended = 0;
            _resets = 0;
            return counts;
        }

        /// <summary>
        /// This frame's shift of every march's jitter, 0..1 (the golden ratio's walk), for the "grain
        /// changes every frame" test switch: the same pixel's jitter then visits all of 0..1 over the frames.
        /// </summary>
        public static float FrameShift(int frame)
        {
            double shift = (frame & 0xFFFFF) * 0.6180339887498949;
            return (float)(shift - Math.Floor(shift));
        }

        /// <summary>
        /// Marches and blends this frame's clouds for <paramref name="camera"/>, about to draw the box with
        /// <paramref name="clouds"/>, and points the material at the blend. False: not drawn (the material
        /// must march as before).
        /// </summary>
        public bool Render(Camera camera, Material clouds)
        {
            if (_failed)
                return false;

            // The same camera drawn twice in a frame (a screenshot): the blend already there.
            int frame = Time.frameCount;
            if (frame == _lastFrame)
                return _hasHistory;

            // The march's size (SmootherSize, the row "Cloud drawing size"): the size the camera renders at (a mod that
            // renders the game's camera into a smaller target of its own gets the march for that size), but never
            // of more lines than the screen has (2026-11-18: a mod supersampling at 200% had the march at four times
            // any other player's pixels) -- divided by 2 (the default, as always), 3 or 4 each way.
            int choice = Settings.SmootherCloudsSize != null ? Settings.SmootherCloudsSize.value : Settings.Defaults.SmootherCloudsSize;
            int basisWidth, basisHeight;
            SmootherSize.Basis(camera.pixelWidth, camera.pixelHeight, ScreenLines > 0 ? ScreenLines : Screen.height, out basisWidth, out basisHeight);
            int divisor = SmootherSize.Divisor(choice, basisHeight);
            int width = SmootherSize.Size(basisWidth, divisor);
            int height = SmootherSize.Size(basisHeight, divisor);

            // The smaller march (UpFactor): the blend keeps that size, the march is smaller again each way. Only the
            // blend against smearing builds the larger picture up: 1.5.3's (the test row unticked) keeps its own size.
            // And only on a screen (or a smaller render) of UpFromLines lines or more: under that the march keeps its size.
            bool sharpBlend = Settings.SmootherCloudsSharp == null || Settings.SmootherCloudsSharp.value;
            int up = sharpBlend && basisHeight >= UpFromLines ? Mathf.Clamp(UpFactor, 1, 4) : 1;
            int marchWidth = SmootherSize.Size(width, up);
            int marchHeight = SmootherSize.Size(height, up);
            if (_march == null || _history[0] == null || _history[0].width != width || _history[0].height != height ||
                _march.width != marchWidth || _march.height != marchHeight)
            {
                // A one-off draw at another size (the game's screenshot) marches as before, at its own
                // full size, and keeps the blend; a size that stays (a resolution change) gets new targets.
                // The player's own change of the row is no one-off: new targets at once.
                _otherSize++;
                bool rowMoved = _march != null && choice != _madeChoice;
                if (_march != null && _otherSize < 3 && !rowMoved)
                    return false;
                if (!Create(width, height, marchWidth, marchHeight, choice, divisor, Of(basisWidth, basisHeight, camera.pixelWidth, camera.pixelHeight)))
                    return false;
            }
            else if (choice != _madeChoice)
            {
                // The row moved to a choice that gives this render the same size: the targets stay as they are.
                _madeChoice = choice;
                Log.Msg("smoother clouds: drawing size now " + SmootherSize.ChoiceName(choice) + " -- still " + width + "x" + height +
                        " (" + SmootherSize.DivisorName(divisor) + " of " + Of(basisWidth, basisHeight, camera.pixelWidth, camera.pixelHeight) + " each way)");
            }

            _otherSize = 0;

            // Unjittered, so another mod's TAA (Render It!'s) adds nothing to the move between frames.
            Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
            Matrix4x4 viewProjection = projection * camera.worldToCameraMatrix;
            Matrix4x4 inverse = projection.inverse;
            Matrix4x4 toWorld = camera.cameraToWorldMatrix;
            Vector3 eye = camera.transform.position;
            Vector3 forward = camera.transform.forward;

            // This frame's place inside each texel (R2, Roberts 2018), as a shift of the camera's rays.
            _marches++;
            double sx = 0.5 + _marches * 0.7548776662466927;
            double sy = 0.5 + _marches * 0.5698402909980532;
            float shiftX = (float)(sx - Math.Floor(sx)) - 0.5f;
            float shiftY = (float)(sy - Math.Floor(sy)) - 0.5f;
            Vector4 size = new Vector4(marchWidth, marchHeight, 1f / marchWidth, 1f / marchHeight);

            SetCorners(inverse, toWorld, shiftX * 2f / marchWidth, shiftY * 2f / marchHeight);
            _block.SetFloat(IdTemporalMode, 1f);
            _block.SetVector(IdCorner0, _corners[0]);
            _block.SetVector(IdCorner1, _corners[1]);
            _block.SetVector(IdCorner2, _corners[2]);
            _block.SetVector(IdCorner3, _corners[3]);
            _block.SetVector(IdTemporalEye, new Vector4(eye.x, eye.y, eye.z, _up));
            _block.SetVector(IdTemporalSize, size);
            _block.SetFloat(IdTemporalJitter, FrameShift(frame));

            // How far the wind carried the clouds since last frame (the weather map's drift, which the
            // shapes follow), wrapped like the map: a jump (a new pattern, a moved cloud shift) starts afresh.
            Vector4 phase = clouds.GetVector(IdWeatherPhase);
            float tile = clouds.GetFloat(IdWeatherTile);
            Vector2 drift = new Vector2(Wrap(phase.x - _previousPhase.x), Wrap(phase.y - _previousPhase.y)) * tile;

            bool fresh = !_hasHistory ||
                         (eye - _previousEye).sqrMagnitude > CutDistance * CutDistance ||
                         Vector3.Angle(forward, _previousForward) > CutAngle ||
                         drift.magnitude > 2000f;
            if (fresh)
                _resets++;

            float keep = fresh ? 0f : (clouds.GetFloat(IdFlashCount) > 0.5f ? KeepInFlash : Keep);

            SetCorners(inverse, toWorld, 0f, 0f);
            _resolve.SetVector(IdCorner0, _corners[0]);
            _resolve.SetVector(IdCorner1, _corners[1]);
            _resolve.SetVector(IdCorner2, _corners[2]);
            _resolve.SetVector(IdCorner3, _corners[3]);
            _resolve.SetVector(IdTemporalEye, new Vector4(eye.x, eye.y, eye.z, _up));
            _resolve.SetVector(IdTemporalSize, size);
            _resolve.SetMatrix(IdTemporalPrevVP, _previousVP);
            // w: the 2026-11-15 blend against smearing (1) or 1.5.3's (0), by the test row.
            bool sharp = Settings.SmootherCloudsSharp == null || Settings.SmootherCloudsSharp.value;
            if (sharp != _loggedSharp)
            {
                _loggedSharp = sharp;
                Log.Msg("smoother clouds: blend " + (sharp
                    ? "against smearing -- three depths per pixel (base, middle, far side), variance clip, less kept where the depths disagree (a turn keeps it all), the march softened where history is given up"
                    : "as in 1.5.3 -- the base plane, min/max clamp"));
            }
            _resolve.SetVector(IdTemporalParams, new Vector4(keep, drift.x, drift.y, sharp ? 1f : 0f));
            // The smaller march: this frame's shift (the blend puts each ray back where it landed) and the factor.
            // A fresh start (nothing kept): every pixel takes all of this frame, read between its rays -- never a share of
            // the old picture, which the blend's target still holds.
            _resolve.SetVector(IdTemporalUp, new Vector4(shiftX, shiftY, up, keep > 0f ? UpTake : 1e6f));
            _resolve.SetVector(IdTemporalUp2, new Vector4(UpSharp, UpLeast, UpClip, 0f));
            // A pass has 16 samplers and the cloud shader had one left: the blend reads this frame's
            // march through _TemporalTex and the blend so far through the rainbow's _BowTex (never the
            // rainbow on this material).
            _resolve.SetTexture(IdTemporalTex, _march);
            _resolve.SetTexture(IdBowTex, _history[_current]);
            _resolve.SetFloat(IdCloudBottom, clouds.GetFloat(IdCloudBottom));
            _resolve.SetFloat(IdCloudTop, clouds.GetFloat(IdCloudTop));
            _resolve.SetFloat(IdMaxDistance, clouds.GetFloat(IdMaxDistance));

            int next = 1 - _current;
            _buffer.Clear();
            _buffer.SetRenderTarget(_march);
            _buffer.ClearRenderTarget(false, true, Color.clear);
            _buffer.DrawMesh(_quad, Matrix4x4.identity, clouds, 0, 0, _block);
            _buffer.SetRenderTarget(_history[next]);
            _buffer.ClearRenderTarget(false, true, Color.clear);
            _buffer.DrawMesh(_quad, Matrix4x4.identity, _resolve, 0, 0);
            Graphics.ExecuteCommandBuffer(_buffer);

            _current = next;
            _hasHistory = true;
            _lastFrame = frame;
            _blended++;
            _previousVP = viewProjection;
            _previousEye = eye;
            _previousForward = forward;
            _previousPhase = new Vector2(phase.x, phase.y);

            // For the camera's draw, which looks each ray up through this frame's matrix -- only from where
            // it was marched (the shader checks the eye: any other camera marches).
            clouds.SetTexture(IdTemporalTex, _history[_current]);
            clouds.SetMatrix(IdTemporalVP, viewProjection);
            clouds.SetVector(IdTemporalEye, new Vector4(eye.x, eye.y, eye.z, _up));
            return true;
        }

        /// <summary>
        /// The camera's rays (world, to the far plane, not normalised) at the target's four corners, the
        /// whole picture shifted by (<paramref name="ndcX"/>, <paramref name="ndcY"/>) in clip space.
        /// Rays to a plane change linearly across the screen, so the quad's interpolation between them is
        /// each texel's own ray.
        /// </summary>
        private void SetCorners(Matrix4x4 inverseProjection, Matrix4x4 toWorld, float ndcX, float ndcY)
        {
            for (int k = 0; k < 4; k++)
            {
                float u = k % 2;
                float v = k / 2;
                Vector4 p = inverseProjection * new Vector4(u * 2f - 1f + ndcX, v * 2f - 1f + ndcY, 1f, 1f);
                Vector3 view = new Vector3(p.x, p.y, p.z) / p.w;
                Vector3 world = toWorld.MultiplyVector(view);
                _corners[k] = new Vector4(world.x, world.y, world.z, 0f);
            }
        }

        /// <summary>A phase difference wrapped to -0.5..0.5 of a tile.</summary>
        private static float Wrap(float d)
        {
            return d - Mathf.Floor(d + 0.5f);
        }

        /// <summary>
        /// The screen's lines for the march's size; 0 (always, in the game): Screen.height. A test harness whose
        /// editor has no screen of the game's size sets it.
        /// </summary>
        public static int ScreenLines;

        /// <summary>For the log: what the march's size is a share of -- the render, or the screen's size when the camera renders more.</summary>
        private static string Of(int basisWidth, int basisHeight, int renderWidth, int renderHeight)
        {
            return basisHeight == renderHeight
                ? "the " + renderWidth + "x" + renderHeight + " render"
                : basisWidth + "x" + basisHeight + ", the screen's lines: the camera renders " + renderWidth + "x" + renderHeight +
                  ", more than the screen shows";
        }

        /// <summary>
        /// THE SMALLER MARCH (2026-11-18; 1 = off, the march at the blend's own size as it always was). At 2 the march
        /// is half the blend's size each way (a quarter of its pixels); the blend puts each frame's rays back where
        /// they landed (the march is moved by a part of a texel every frame) and builds the larger picture up over
        /// a few frames (CloudRaymarchPass.cginc TemporalResolve, _TemporalUp). Measured in the cloud bench, Auto at
        /// 81% cover with Softer cumulus, 4K screen: 15.8-20.1 ms a frame at 1, 4.8-5.8 at 2, the fringes' detail kept
        /// still and moving where a plain quarter-size march blurs them. Unticking the test row "blend against
        /// smearing" gives 1.5.3's blend and the march at its old size.
        /// </summary>
        public static int UpFactor = 2;

        /// <summary>
        /// The smaller march only where the size the clouds are drawn for (the screen, or a smaller render) has this
        /// many lines or more: 4K and the like, where the march is the frame's largest cost. On a 1440p or 1080p
        /// screen the march would fall to 360 or 270 lines, which nobody has looked at yet: there it keeps its size.
        /// By the screen and not by the blend, so the row's smaller sizes stay the faster ones.
        /// </summary>
        public static int UpFromLines = 1600;

        /// <summary>The smaller march: how many times the usual share of a frame a ray right on a pixel gives.</summary>
        public static float UpTake = 2.5f;

        /// <summary>The smaller march: how fast a ray's share falls with its distance from the pixel (exp(-x d^2), in pixels).</summary>
        public static float UpSharp = 2.5f;

        /// <summary>The smaller march: the least share a ray far from the pixel still gives (of the full one).</summary>
        public static float UpLeast = 0.15f;

        /// <summary>
        /// The smaller march: the variance clip's width, times the usual. (Five settings of these four numbers tried in
        /// the bench, 2026-11-18, still / pan / strafe / dolly: all within 0.05/255 of each other against the full-size
        /// march; a clip of 1.6 a little nearer to it than 1.)
        /// </summary>
        public static float UpClip = 1.3f;

        private bool Create(int width, int height, int marchWidth, int marchHeight, int choice, int divisor, string of)
        {
            Release(false);
            _madeChoice = choice;

            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                _failed = true;
                Log.Msg("smoother clouds: not available -- this graphics card can't draw into ARGBHalf targets; the clouds are marched as before");
                return false;
            }

            _march = NewTarget("VolumetricCloudsTemporalMarch", marchWidth, marchHeight);
            _history[0] = NewTarget("VolumetricCloudsTemporalHistoryA", width, height);
            _history[1] = NewTarget("VolumetricCloudsTemporalHistoryB", width, height);
            _buffer = new CommandBuffer { name = "VolumetricClouds smoother clouds" };
            _up = GL.GetGPUProjectionMatrix(Matrix4x4.identity, true).m11 < 0f ? -1f : 1f;
            _current = 0;
            _hasHistory = false;

            float megabytes = (2f * width * height + (float)marchWidth * marchHeight) * 8f / (1024f * 1024f);
            Log.Msg("smoother clouds: on -- the clouds marched at " + marchWidth + "x" + marchHeight +
                    (marchWidth != width ? " and built up to " + width + "x" + height + " over the frames" : "") +
                    " (" + SmootherSize.DivisorName(divisor) + " of " + of + " each way; drawing size: " +
                    SmootherSize.ChoiceName(choice) + ") every frame and blended with the frames before (keep " +
                    Keep.ToString("F2") + ", " + KeepInFlash.ToString("F2") + " in a flash); 3 ARGBHalf targets, " +
                    megabytes.ToString("F0") + " MB; target up " + _up +
                    "; pixels with a building or hill inside the clouds, or a bolt, marched as before");
            return true;
        }

        private static RenderTexture NewTarget(string name, int width, int height)
        {
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
            };
            target.Create();
            return target;
        }

        /// <summary>Lets the targets go (switched off, the clouds hidden, the city closing).</summary>
        public void Release()
        {
            Release(true);
        }

        private void Release(bool log)
        {
            bool had = _march != null;

            if (_march != null)
            {
                _march.Release();
                UnityEngine.Object.Destroy(_march);
                _march = null;
            }

            for (int k = 0; k < _history.Length; k++)
            {
                if (_history[k] != null)
                {
                    _history[k].Release();
                    UnityEngine.Object.Destroy(_history[k]);
                    _history[k] = null;
                }
            }

            if (_buffer != null)
            {
                _buffer.Release();
                _buffer = null;
            }

            _hasHistory = false;
            _lastFrame = -1;

            if (had && log)
                Log.Msg("smoother clouds: off, targets released; the clouds are marched as before");
        }

        public void Dispose()
        {
            Release(false);
            if (_resolve != null)
                UnityEngine.Object.Destroy(_resolve);
            if (_quad != null)
                UnityEngine.Object.Destroy(_quad);
        }
    }
}
