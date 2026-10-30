using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The tree bench's renders (tools/tree-bench.ps1 -Part Render; the 5.6 editor WITH graphics, legacy
/// deferred HDR and linear as the game). Every line it logs starts "TREE BENCH"; a failed check says FAIL.
/// - SAME AS THE GAME: a small scene (two near trees, one turned and scaled as Tree Control may, three mid
///   billboards, a far billboard mesh, a ground in their shadows) drawn with the twins (TreeBenchGame*.shader,
///   VW's wind compiled out; their programs are the game's, the -Part Asm check) and with the shipped shaders
///   at _VWTreeWind 0: every pixel the same, sway, shadows and snow included; and the scene not empty, the
///   game's sway moving it, VW's wind moving it when on.
/// - THE WIND'S TWIN: Trees.cginc's VWWindPoint on a few hundred points, written to wind-points.tsv for the
///   C# twin (Sky/TreeWind.PointAt), which tree-bench.ps1 compares.
/// - MOTION VECTORS: whether a shader's own MotionVectors pass reaches the camera's motion-vector texture for
///   a renderer and for Graphics.DrawMesh (the game's near trees).
/// </summary>
public static class TreeBenchRun
{
    private const int Width = 320, Height = 240;
    public const string WindPoints = "TreeBench/wind-points.tsv";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static int _fails;

    public static void Run()
    {
        _fails = 0;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        try
        {
            SameAsTheGame();
            WindTwin();
            MotionVectors();
        }
        catch (System.Exception e)
        {
            Line("FAIL: an exception " + e);
            _fails++;
        }

        Line(_fails == 0 ? "renders: all passed" : "renders: " + _fails + " FAIL(s)");
    }

    private static void Line(string s)
    {
        Debug.Log("TREE BENCH " + s);
    }

    private static void Check(bool ok, string what)
    {
        Line((ok ? "PASS " : "FAIL ") + what);
        if (!ok)
            _fails++;
    }

    // ---------------------------------------------------------------- the scene

    private static Shader Load(string file)
    {
        Shader s = AssetDatabase.LoadAssetAtPath<Shader>("Assets/VolumetricClouds/" + file);
        if (s == null || !s.isSupported)
            throw new System.Exception(file + " not loaded or not supported");
        return s;
    }

    private static Texture2D Tex(int size, System.Func<int, int, Color> f, bool linear)
    {
        Texture2D t = new Texture2D(size, size, TextureFormat.RGBAFloat, false, linear);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                t.SetPixel(x, y, f(x, y));
        t.Apply();
        t.wrapMode = TextureWrapMode.Repeat;
        return t;
    }

    /// <summary>A small tree: a trunk and three crossed leaf cards; colour alpha (the sway weight) 0 at the foot, 1 at the top.</summary>
    private static Mesh NearTree()
    {
        List<Vector3> v = new List<Vector3>();
        List<Vector3> n = new List<Vector3>();
        List<Vector4> t = new List<Vector4>();
        List<Vector2> uv = new List<Vector2>();
        List<Color> c = new List<Color>();
        List<int> tri = new List<int>();
        System.Action<Vector3, Vector3, float, float> card = (centre, right, w, h) =>
        {
            Vector3 up = Vector3.up;
            int b = v.Count;
            Vector3 normal = Vector3.Cross(right, up).normalized;
            for (int j = 0; j <= 4; j++)
                for (int i = 0; i <= 2; i++)
                {
                    float u = i / 2f, s = j / 4f;
                    Vector3 p = centre + right * (u - 0.5f) * w + up * s * h;
                    v.Add(p);
                    n.Add(normal);
                    t.Add(new Vector4(right.x, right.y, right.z, 1f));
                    uv.Add(new Vector2(u, s));
                    c.Add(new Color(1f, 1f, 1f, Mathf.Clamp01(p.y / 10f)));
                }
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 2; i++)
                {
                    int a = b + j * 3 + i;
                    tri.AddRange(new[] { a, a + 3, a + 1, a + 1, a + 3, a + 4 });
                }
        };
        card(Vector3.zero, Vector3.right, 0.8f, 4f);
        for (int k = 0; k < 3; k++)
        {
            float a = k * 60f * Mathf.Deg2Rad;
            card(new Vector3(0f, 3f, 0f), new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)), 6f, 8f);
        }

        Mesh m = new Mesh();
        m.SetVertices(v);
        m.SetNormals(n);
        m.SetTangents(t);
        m.SetUVs(0, uv);
        m.SetColors(c);
        m.SetTriangles(tri, 0);
        m.bounds = new Bounds(Vector3.zero, Vector3.one * 2000f);
        return m;
    }

    private static readonly Vector2[] Corner = { new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f) };

    /// <summary>The mid trees' mesh: instance i's four corners, its index in z, the offset in uv1.</summary>
    private static Mesh MidTrees(int count, Vector2 half)
    {
        List<Vector3> v = new List<Vector3>();
        List<Vector2> uv = new List<Vector2>();
        List<Vector2> uv1 = new List<Vector2>();
        List<int> tri = new List<int>();
        for (int i = 0; i < count; i++)
        {
            int b = v.Count;
            foreach (Vector2 k in Corner)
            {
                Vector2 off = Vector2.Scale(k, half);
                v.Add(new Vector3(off.x, off.y, i));
                uv.Add(new Vector2(k.x * 0.5f + 0.5f, k.y * 0.5f + 0.5f));
                uv1.Add(off);
            }
            tri.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }

        Mesh m = new Mesh();
        m.SetVertices(v);
        m.SetUVs(0, uv);
        m.SetUVs(1, uv1);
        m.SetTriangles(tri, 0);
        m.bounds = new Bounds(Vector3.zero, Vector3.one * 5000f);
        return m;
    }

    /// <summary>The far layer's mesh as TreeInstance.PopulateGroupData bakes it: the corner at the tree + offset, the offset in uv1, colour (a = wind / 2).</summary>
    private static Mesh FarTrees(Vector3[] trees, Vector2 half, float wind)
    {
        List<Vector3> v = new List<Vector3>();
        List<Vector3> n = new List<Vector3>();
        List<Vector2> uv = new List<Vector2>();
        List<Vector2> uv1 = new List<Vector2>();
        List<Color32> c = new List<Color32>();
        List<int> tri = new List<int>();
        foreach (Vector3 p in trees)
        {
            int b = v.Count;
            foreach (Vector2 k in Corner)
            {
                Vector2 off = Vector2.Scale(k, half);
                v.Add(p + new Vector3(off.x, off.y, 0f));
                n.Add(Vector3.up);
                uv.Add(new Vector2(k.x * 0.5f + 0.5f, k.y * 0.5f + 0.5f));
                uv1.Add(off);
                c.Add(new Color32(200, 230, 190, (byte)Mathf.Clamp(Mathf.RoundToInt(wind * 128f), 0, 255)));
            }
            tri.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
        }

        Mesh m = new Mesh();
        m.SetVertices(v);
        m.SetNormals(n);
        m.SetUVs(0, uv);
        m.SetUVs(1, uv1);
        m.SetColors(c);
        m.SetTriangles(tri, 0);
        m.bounds = new Bounds(Vector3.zero, Vector3.one * 5000f);
        return m;
    }

    private class Scene
    {
        public Camera Cam;
        public Light Sun;
        public RenderTexture Target;
        public MeshRenderer[] Near = new MeshRenderer[2];
        public MeshRenderer Mid, Far;
        public Material[] NearMat = new Material[2], MidMat = new Material[2], FarMat = new Material[2];
        public List<GameObject> Made = new List<GameObject>();
    }

    private static Scene Build(Shader defaultTwin, Shader defaultOurs, Shader groupTwin, Shader groupOurs)
    {
        Scene s = new Scene();
        Texture2D leaf = Tex(64, (x, y) => new Color(0.2f + x / 128f, 0.5f + y / 160f, 0.15f, (x / 8 + y / 8) % 3 == 0 ? 0.2f : 1f), false);
        // XYCA: x, y the normal map, z the tint share, a the cut-out (the near trees: cut where 0.5 - a < 0).
        Texture2D xyca = Tex(64, (x, y) => new Color(0.5f + 0.3f * Mathf.Sin(x * 0.3f), 0.5f + 0.3f * Mathf.Cos(y * 0.2f), 0.7f, ((x / 6 + y / 5) % 4 == 0) ? 0.8f : 0.1f), true);
        Texture2D xycaFar = Tex(64, (x, y) => new Color(0.5f + 0.3f * Mathf.Sin(x * 0.3f), 0.5f + 0.3f * Mathf.Cos(y * 0.2f), 0.7f, ((x / 6 + y / 5) % 4 == 0) ? 0.1f : 0.9f), true);
        Texture2D black = Tex(4, (x, y) => new Color(0f, 0f, 0f, 0f), true);
        Texture2D ruin = Tex(16, (x, y) => new Color(0.4f, 0.3f, 0.2f, 1f), false);
        Texture2D snow = Tex(16, (x, y) => new Color(0.95f, 0.95f, 1f, (x + y) % 5 / 4f), false);

        Shader.SetGlobalTexture("_NaturalResources", black);
        Shader.SetGlobalTexture("_NaturalDestruction", black);
        Shader.SetGlobalTexture("_TerrainRuinedDiffuse", ruin);
        Shader.SetGlobalTexture("_TerrainGrassDiffuse", snow);
        Shader.SetGlobalVector("_NaturalResourceMapping", new Vector4(0.5f, 0.5f, 1f / 17280f, 0f));
        Shader.SetGlobalVector("_TerrainTextureTiling1", new Vector4(0.05f, 0.07f, 0f, 0f));
        Shader.SetGlobalVector("_TerrainTextureTiling2", new Vector4(0.13f, 0f, 0f, 0f));
        Shader.SetGlobalVector("_WindDirection", new Vector4(0.6f, 0f, 0.8f, 0.9f));
        Shader.SetGlobalVector("_AmbientColor", new Vector4(0.3f, 0.35f, 0.42f, 0.6f));
        Shader.SetGlobalFloat("_DisableSnow", 0f);

        GameObject camGo = new GameObject("bench camera");
        s.Made.Add(camGo);
        s.Cam = camGo.AddComponent<Camera>();
        s.Cam.renderingPath = RenderingPath.DeferredLighting;
        s.Cam.allowHDR = true;
        s.Cam.fieldOfView = 40f;
        s.Cam.nearClipPlane = 0.3f;
        s.Cam.farClipPlane = 3000f;
        s.Cam.clearFlags = CameraClearFlags.SolidColor;
        s.Cam.backgroundColor = new Color(0.4f, 0.5f, 0.7f, 1f);
        s.Cam.transform.position = new Vector3(2f, 9f, -32f);
        s.Cam.transform.LookAt(new Vector3(4f, 5f, 10f));
        s.Target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        s.Cam.targetTexture = s.Target;
        Shader.SetGlobalVector("_CameraRight", s.Cam.transform.right);
        Shader.SetGlobalVector("_CameraUp", s.Cam.transform.up);
        Shader.SetGlobalVector("_CameraForward", s.Cam.transform.forward);

        GameObject sunGo = new GameObject("bench sun");
        s.Made.Add(sunGo);
        s.Sun = sunGo.AddComponent<Light>();
        s.Sun.type = LightType.Directional;
        s.Sun.shadows = LightShadows.Hard;
        s.Sun.shadowStrength = 1f;
        s.Sun.intensity = 1.2f;
        sunGo.transform.rotation = Quaternion.Euler(35f, 60f, 0f);
        Shader.SetGlobalVector("_ShadowRight", sunGo.transform.right);
        Shader.SetGlobalVector("_ShadowUp", sunGo.transform.up);
        Shader.SetGlobalVector("_ShadowForward", sunGo.transform.forward);
        QualitySettings.shadowDistance = 300f;
        QualitySettings.shadowCascades = 1;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.4f, 0.45f, 0.55f);
        RenderSettings.ambientEquatorColor = new Color(0.3f, 0.3f, 0.3f);
        RenderSettings.ambientGroundColor = new Color(0.2f, 0.18f, 0.15f);

        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        s.Made.Add(ground);
        ground.transform.localScale = new Vector3(40f, 1f, 40f);
        ground.GetComponent<MeshRenderer>().sharedMaterial = new Material(Shader.Find("Legacy Shaders/Diffuse"));

        Mesh near = NearTree();
        Vector3[] nearAt = { new Vector3(-3f, 0f, 2f), new Vector3(9f, 0f, 6f) };
        Quaternion[] nearTurn = { Quaternion.identity, Quaternion.Euler(0f, 40f, 0f) };
        float[] nearScale = { 1f, 1.3f };
        for (int i = 0; i < 2; i++)
        {
            GameObject go = new GameObject("near tree " + i);
            s.Made.Add(go);
            go.transform.position = nearAt[i];
            go.transform.rotation = nearTurn[i];
            go.transform.localScale = Vector3.one * nearScale[i];
            go.AddComponent<MeshFilter>().sharedMesh = near;
            s.Near[i] = go.AddComponent<MeshRenderer>();
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            block.SetColor("_Color", new Color(0.9f, 1f, 0.8f, i == 0 ? 1f : 1.6f));
            block.SetFloat("_DisableRuined", 0f);
            s.Near[i].SetPropertyBlock(block);
        }

        s.NearMat[0] = Material(defaultTwin, leaf, xyca);
        s.NearMat[1] = Material(defaultOurs, leaf, xyca);
        foreach (Material m in s.NearMat)
            m.SetFloat("_VWTreeSize", 12f);

        Vector2 half = new Vector2(5f, 6f);
        GameObject midGo = new GameObject("mid trees");
        s.Made.Add(midGo);
        midGo.AddComponent<MeshFilter>().sharedMesh = MidTrees(3, half);
        s.Mid = midGo.AddComponent<MeshRenderer>();
        MaterialPropertyBlock mid = new MaterialPropertyBlock();
        Vector4[] location = new Vector4[16];
        Vector4[] colour = new Vector4[16];
        float[] ruined = new float[16];
        location[0] = new Vector4(-14f, 6f, 20f, 1f);
        location[1] = new Vector4(16f, 7.2f, 28f, 1.2f);
        location[2] = new Vector4(2f, 6f, 45f, 1f);
        for (int i = 0; i < 3; i++)
            colour[i] = new Vector4(0.8f, 1f, 0.85f, 0.7f + 0.5f * i);
        mid.SetVectorArray("_TreeLocation", location);
        mid.SetVectorArray("_TreeColor", colour);
        mid.SetFloatArray("_TreeDisableRuined", ruined);
        s.Mid.SetPropertyBlock(mid);
        s.MidMat[0] = Material(groupTwin, leaf, xycaFar);
        s.MidMat[1] = Material(groupOurs, leaf, xycaFar);
        foreach (Material m in s.MidMat)
            m.EnableKeyword("MULTI_INSTANCE");

        GameObject farGo = new GameObject("far trees");
        s.Made.Add(farGo);
        farGo.transform.position = new Vector3(-30f, 0f, 60f);
        farGo.AddComponent<MeshFilter>().sharedMesh = FarTrees(new[] { new Vector3(0f, 6f, 0f), new Vector3(14f, 6f, 9f), new Vector3(30f, 6f, 3f) }, half, 1.1f);
        s.Far = farGo.AddComponent<MeshRenderer>();
        s.FarMat[0] = Material(groupTwin, leaf, xycaFar);
        s.FarMat[1] = Material(groupOurs, leaf, xycaFar);
        return s;
    }

    private static Material Material(Shader shader, Texture2D leaf, Texture2D xyca)
    {
        Material m = new Material(shader);
        m.SetTexture("_MainTex", leaf);
        m.SetTexture("_XYCAMap", xyca);
        m.SetTexture("_ShadowAMap", xyca);
        m.SetColor("_Color", Color.white);
        return m;
    }

    private static Color[] Render(Scene s, int which)
    {
        foreach (MeshRenderer r in s.Near)
            r.sharedMaterial = s.NearMat[which];
        s.Mid.sharedMaterial = s.MidMat[which];
        s.Far.sharedMaterial = s.FarMat[which];
        s.Cam.Render();
        RenderTexture.active = s.Target;
        Texture2D read = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
        read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
        RenderTexture.active = null;
        Color[] px = read.GetPixels();
        Object.DestroyImmediate(read);
        return px;
    }

    private static void Compare(Color[] a, Color[] b, out float most, out int differ)
    {
        most = 0f;
        differ = 0;
        for (int i = 0; i < a.Length; i++)
        {
            float d = Mathf.Max(Mathf.Max(Mathf.Abs(a[i].r - b[i].r), Mathf.Abs(a[i].g - b[i].g)), Mathf.Abs(a[i].b - b[i].b));
            if (d > 0f)
                differ++;
            most = Mathf.Max(most, d);
        }
    }

    private static void SetWind(bool on, float lean)
    {
        Shader.SetGlobalVector("_VWTreeWind", new Vector4(on ? 1f : 0f, lean, 0.5f, 0.06f));
        Shader.SetGlobalVector("_VWTreeWindDir", new Vector4(0.6f, 0f, 0.8f, 0.33f));
        Shader.SetGlobalVector("_VWTreeGust", new Vector4(310f, -120f, 1f / 160f, 0.35f));
        Shader.SetGlobalVector("_VWTreeClock", new Vector4(64f, 0.25f, 0f, 0f));
    }

    private static void SameAsTheGame()
    {
        Shader defaultTwin = Load("TreeBenchGameDefault.shader");
        Shader groupTwin = Load("TreeBenchGameGroup.shader");
        Shader defaultOurs = Load("TreesDefault.shader");
        Shader groupOurs = Load("TreesGroup.shader");
        Scene s = Build(defaultTwin, defaultOurs, groupTwin, groupOurs);
        try
        {
            Shader.SetGlobalVector("_SimulationTime", new Vector4(123.4f, 0f, 0f, 5.3f));
            SetWind(false, 0.12f);

            string[] cases = { "near + billboards, HDR", "near + billboards, no HDR", "snow (SNOW_ON), HDR" };
            for (int c = 0; c < cases.Length; c++)
            {
                s.Cam.allowHDR = c != 1;
                Shader.DisableKeyword(c == 2 ? "SHADING" : "SNOW_ON");
                Shader.EnableKeyword(c == 2 ? "SNOW_ON" : "SHADING");
                Color[] twin = Render(s, 0);
                Color[] ours = Render(s, 1);
                float most;
                int differ;
                Compare(twin, ours, out most, out differ);
                Check(differ == 0, string.Format(Inv, "same as the game, {0}: {1} of {2} pixels differ, most {3:G4}", cases[c], differ, twin.Length, most));
            }

            s.Cam.allowHDR = true;
            Shader.DisableKeyword("SNOW_ON");
            Shader.EnableKeyword("SHADING");
            Color[] base0 = Render(s, 0);

            // Not an empty test: the trees are drawn, they sway, they cast shadows.
            foreach (MeshRenderer r in s.Near) r.enabled = false;
            s.Mid.enabled = false;
            s.Far.enabled = false;
            Color[] bare = Render(s, 0);
            foreach (MeshRenderer r in s.Near) r.enabled = true;
            s.Mid.enabled = true;
            s.Far.enabled = true;
            float most2;
            int drawn;
            Compare(base0, bare, out most2, out drawn);
            Check(drawn > 2000, "the trees are drawn: " + drawn + " pixels are theirs (or their shadows')");

            s.Sun.shadows = LightShadows.None;
            Color[] noShadow = Render(s, 0);
            s.Sun.shadows = LightShadows.Hard;
            int shaded;
            Compare(base0, noShadow, out most2, out shaded);
            Check(shaded > 500, "their shadows are drawn: " + shaded + " pixels change without them");

            Shader.SetGlobalVector("_SimulationTime", new Vector4(123.4f, 0f, 0f, 5.9f));
            Color[] later = Render(s, 0);
            int swayed;
            Compare(base0, later, out most2, out swayed);
            Check(swayed > 200, "the game's sway moves them: " + swayed + " pixels change 0.6 s later");
            Color[] laterOurs = Render(s, 1);
            int differLater;
            Compare(later, laterOurs, out most2, out differLater);
            Check(differLater == 0, "same as the game 0.6 s later too: " + differLater + " pixels differ");

            Shader.SetGlobalVector("_SimulationTime", new Vector4(123.4f, 0f, 0f, 5.3f));
            SetWind(true, 0.12f);
            Color[] windy = Render(s, 1);
            int moved;
            Compare(base0, windy, out most2, out moved);
            Check(moved > 1000, "VW's wind moves them when on: " + moved + " pixels change");
            SetWind(false, 0.12f);
        }
        finally
        {
            foreach (GameObject go in s.Made)
                Object.DestroyImmediate(go);
            Object.DestroyImmediate(s.Target);
        }
    }

    // ---------------------------------------------------------------- the wind's twin

    private static void WindTwin()
    {
        WindTwin(0.6f, WindPoints);
        WindTwin(0f, WindPoints.Replace(".tsv", "-nogusts.tsv"));
    }

    private static void WindTwin(float gusts, string file)
    {
        Shader probe = Load("TreeBenchWindProbe.shader");
        System.Random rnd = new System.Random(7);
        int count = 512;
        List<Vector3> pos = new List<Vector3>();
        List<Vector4> t0 = new List<Vector4>(), t1 = new List<Vector4>(), t2 = new List<Vector4>(), t3 = new List<Vector4>();
        StringBuilder tsv = new StringBuilder();
        float lean = 0.09f, flutter = 0.12f, clock = 0.6875f, gx = 7310.5f, gz = 15200.25f;
        Vector2 dir = new Vector2(0.28f, -0.96f);
        tsv.AppendLine(string.Format(Inv, "# globals\t{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}\t{10}\t{11}",
            lean, gusts, flutter, dir.x, dir.y, clock, gx, gz, 1f / 160f, 0.35f, 64f, 0.25f));
        tsv.AppendLine("# px\tpy\tpz\tbx\tby\tbz\tplaceX\tplaceZ\theight\tspeed\tflutter\tnx\tny\tnz\tgpuX\tgpuY\tgpuZ");
        for (int i = 0; i < count; i++)
        {
            Vector3 b = new Vector3((float)(rnd.NextDouble() * 17000 - 8500), (float)(rnd.NextDouble() * 200), (float)(rnd.NextDouble() * 17000 - 8500));
            float height = (float)(2 + rnd.NextDouble() * 30);
            Vector3 p = b + new Vector3((float)(rnd.NextDouble() * 8 - 4), (float)(rnd.NextDouble() * height * 1.1 - 1), (float)(rnd.NextDouble() * 8 - 4));
            Vector3 n = new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f).normalized;
            float speed = (float)(rnd.NextDouble() * 2.4);
            float fl = (float)rnd.NextDouble();
            pos.Add(p);
            t0.Add(new Vector4(b.x, b.y, b.z, height));
            t1.Add(new Vector4(b.x, b.z, speed, fl));
            t2.Add(new Vector4(n.x, n.y, n.z, 0f));
            t3.Add(new Vector4(i, count, 0f, 0f));
        }

        Mesh points = new Mesh();
        points.SetVertices(pos);
        points.SetUVs(0, t0);
        points.SetUVs(1, t1);
        points.SetUVs(2, t2);
        points.SetUVs(3, t3);
        int[] idx = new int[count];
        for (int i = 0; i < count; i++) idx[i] = i;
        points.SetIndices(idx, MeshTopology.Points, 0);
        points.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);

        Shader.SetGlobalVector("_VWTreeWind", new Vector4(1f, lean, gusts, flutter));
        Shader.SetGlobalVector("_VWTreeWindDir", new Vector4(dir.x, 0f, dir.y, clock));
        Shader.SetGlobalVector("_VWTreeGust", new Vector4(gx, gz, 1f / 160f, 0.35f));
        Shader.SetGlobalVector("_VWTreeClock", new Vector4(64f, 0.25f, 0f, 0f));

        RenderTexture rt = new RenderTexture(count, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        rt.Create();
        Material mat = new Material(probe);
        CommandBuffer cb = new CommandBuffer();
        cb.SetRenderTarget(rt);
        cb.ClearRenderTarget(false, true, Color.clear);
        cb.DrawMesh(points, Matrix4x4.identity, mat, 0, 0);
        Graphics.ExecuteCommandBuffer(cb);
        RenderTexture.active = rt;
        Texture2D read = new Texture2D(count, 1, TextureFormat.RGBAFloat, false, true);
        read.ReadPixels(new Rect(0, 0, count, 1), 0, 0, false);
        RenderTexture.active = null;
        Color[] got = read.GetPixels();

        // The gust and its octaves at each place (the probe's pass 1).
        CommandBuffer cb1 = new CommandBuffer();
        cb1.SetRenderTarget(rt);
        cb1.ClearRenderTarget(false, true, Color.clear);
        cb1.DrawMesh(points, Matrix4x4.identity, mat, 0, 1);
        Graphics.ExecuteCommandBuffer(cb1);
        RenderTexture.active = rt;
        read.ReadPixels(new Rect(0, 0, count, 1), 0, 0, false);
        RenderTexture.active = null;
        Color[] gust = read.GetPixels();

        int written = 0;
        for (int i = 0; i < count; i++)
        {
            Vector4 a = t0[i], c = t1[i], n = t2[i];
            if (got[i].a > 0.5f && gust[i].a > 0.5f)
                written++;
            tsv.AppendLine(string.Format(Inv, "{0:R}\t{1:R}\t{2:R}\t{3:R}\t{4:R}\t{5:R}\t{6:R}\t{7:R}\t{8:R}\t{9:R}\t{10:R}\t{11:R}\t{12:R}\t{13:R}\t{14:R}\t{15:R}\t{16:R}\t{17:R}\t{18:R}\t{19:R}",
                pos[i].x, pos[i].y, pos[i].z, a.x, a.y, a.z, c.x, c.y, a.w, c.z, c.w, n.x, n.y, n.z, got[i].r, got[i].g, got[i].b,
                gust[i].r, gust[i].g, gust[i].b));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file));
        File.WriteAllText(file, tsv.ToString());
        Check(written == count, "the wind's points written by the GPU: " + written + " of " + count + " -> " + file);
        Shader.SetGlobalVector("_VWTreeWind", Vector4.zero);
        Object.DestroyImmediate(read);
        rt.Release();
    }

    // ---------------------------------------------------------------- motion vectors

    private static void MotionVectors()
    {
        Shader probe = Load("TreeBenchMotionProbe.shader");
        Material mat = new Material(probe);
        GameObject camGo = new GameObject("motion camera");
        Camera cam = camGo.AddComponent<Camera>();
        cam.renderingPath = RenderingPath.DeferredLighting;
        cam.allowHDR = true;
        cam.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.clearFlags = CameraClearFlags.SolidColor;
        RenderTexture target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        cam.targetTexture = target;
        RenderTexture motion = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        motion.Create();
        CommandBuffer grab = new CommandBuffer { name = "grab motion" };
        grab.Blit(BuiltinRenderTextureType.MotionVectors, motion);
        cam.AddCommandBuffer(CameraEvent.BeforeImageEffects, grab);
        GameObject sunGo = new GameObject("motion sun");
        Light sun = sunGo.AddComponent<Light>();
        sun.type = LightType.Directional;
        GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh cube = primitive.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(primitive);

        System.Func<string> centre = () =>
        {
            RenderTexture.active = motion;
            Texture2D read = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
            read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0, false);
            RenderTexture.active = null;
            Color c = read.GetPixel(32, 32);
            Object.DestroyImmediate(read);
            return string.Format(Inv, "({0:F4}, {1:F4})", c.r, c.g);
        };

        try
        {
            // A renderer, moved between two frames (the control).
            GameObject box = new GameObject("motion box");
            box.AddComponent<MeshFilter>().sharedMesh = cube;
            MeshRenderer r = box.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            box.transform.localScale = Vector3.one * 4f;
            cam.Render();
            box.transform.position = new Vector3(0.3f, 0f, 0f);
            cam.Render();
            string moved = centre();
            Object.DestroyImmediate(box);

            // Graphics.DrawMesh, as the game draws its near trees: still, then moved.
            Graphics.DrawMesh(cube, Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * 4f), mat, 0, cam);
            cam.Render();
            string drawStill = centre();
            Graphics.DrawMesh(cube, Matrix4x4.TRS(new Vector3(0.3f, 0f, 0f), Quaternion.identity, Vector3.one * 4f), mat, 0, cam);
            cam.Render();
            string drawMoved = centre();

            bool rendererWrites = moved == "(0.2500, 0.1250)";
            bool drawWrites = drawStill == "(0.2500, 0.1250)" || drawMoved == "(0.2500, 0.1250)";
            Line("motion vectors: a renderer moved between frames -> " + moved + (rendererWrites ? " (its own pass ran)" : "") +
                 "; Graphics.DrawMesh still -> " + drawStill + ", moved -> " + drawMoved +
                 (drawWrites ? " -- a DrawMesh tree CAN write its own motion" : " -- a DrawMesh tree CANNOT write its own motion (no renderer: Unity runs no motion pass for it)"));
        }
        finally
        {
            cam.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, grab);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(sunGo);
            Object.DestroyImmediate(target);
            motion.Release();
        }
    }
}
