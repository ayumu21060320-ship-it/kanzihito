using System.Collections.Generic;
using UnityEngine;

// 漢字1文字を立体にして、字の形に関係なく手足の骨を無理やり入れ、よたよた歩かせる。
//
// しくみ
//  1. フォントの字を小さな画像に描いて、濃いところを「字の中」とする
//  2. 字の輪郭をなめらかにたどって（マーチングスクエア）、厚みのある立体にする
//  3. 字を囲む箱の決まった位置に骨（腰・胸・頭・左右の腕・左右の脚）を置く（字の形は見ない）
//  4. 立体の点を、位置で骨にくっつける（下の左半分→左脚、左の端→左腕、上→頭 など）
//  5. 骨を毎フレーム動かして歩かせる
public class KanjiBody : MonoBehaviour
{
    const int FontSize = 64;        // 字を描く大きさ（ピクセル）。大きいほど細かいが重くなる
    const int Pad = 4;              // 字のまわりの余白（ピクセル）
    const float EmHeight = 2f;      // 字の大きさ（メートル）。FontSize ピクセルがこの長さになる
    const float Thickness = 0.45f;  // 字の厚み（メートル）

    const float MaxSpeed = 4f;      // 歩く速さ（メートル/秒）
    const float Accel = 12f;        // 速さの変わりやすさ
    const float JumpSpeed = 6.5f;
    const float Gravity = 20f;
    const float Area = 28f;         // 歩ける範囲（中心からの距離）

    // 骨の番号
    const int Hips = 0, Chest = 1, Head = 2, ArmL = 3, ArmR = 4, LegL = 5, LegR = 6, BoneCount = 7;

    public char Letter { get; private set; }
    public Vector2 MoveInput;       // 歩く向き（x: 右, y: 奥）。KanjiGame が毎フレーム入れる

    Transform body;                 // 体全体（揺れ・弾み用）
    Transform[] bones;
    float w, h;                     // 字の幅と高さ（メートル）
    Vector3 velocity;
    bool grounded = true;
    float phase;                    // 歩きの進み具合
    Vector3 lastDir = Vector3.right;
    float seed;                     // 字ごとに動きを少し変える

    // ---- 作る ----

    // 字を作れなかったときは null を返し、error に理由を入れる
    public static KanjiBody Create(char c, Font font, Material material, out string error)
    {
        error = null;
        float[,] field = RenderGlyph(font, c);
        if (field == null) { error = "その字はフォントに入っていない"; return null; }

        var go = new GameObject("Kanji " + c);
        var kb = go.AddComponent<KanjiBody>();
        kb.Letter = c;
        kb.seed = Random.value * 100f;
        if (!kb.Build(field, material))
        {
            Destroy(go);
            error = "その字は形が無い（空白など）";
            return null;
        }
        return kb;
    }

    // 字をフォントから画像に描いて、各ピクセルの濃さ（0〜1）を返す。描けなければ null
    static float[,] RenderGlyph(Font font, char c)
    {
        font.RequestCharactersInTexture(c.ToString(), FontSize, FontStyle.Normal);
        if (!font.GetCharacterInfo(c, out CharacterInfo ci, FontSize, FontStyle.Normal)) return null;
        int gw = ci.glyphWidth, gh = ci.glyphHeight;
        if (gw <= 0 || gh <= 0) return null;

        int n = Mathf.Max(gw, gh) + Pad * 2;
        var rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, new Color(0, 0, 0, 0));
        GL.PushMatrix();
        GL.LoadPixelMatrix(0, n, 0, n);
        font.material.SetPass(0);
        float x0 = (n - gw) * 0.5f, y0 = (n - gh) * 0.5f;
        GL.Begin(GL.QUADS);
        GL.Color(Color.white);
        GL.TexCoord(ci.uvBottomLeft);  GL.Vertex3(x0, y0, 0);
        GL.TexCoord(ci.uvTopLeft);     GL.Vertex3(x0, y0 + gh, 0);
        GL.TexCoord(ci.uvTopRight);    GL.Vertex3(x0 + gw, y0 + gh, 0);
        GL.TexCoord(ci.uvBottomRight); GL.Vertex3(x0 + gw, y0, 0);
        GL.End();
        GL.PopMatrix();

        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false, true);
        tex.ReadPixels(new Rect(0, 0, n, n), 0, 0, false);
        tex.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        var px = tex.GetPixels32();
        Destroy(tex);
        var f = new float[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                f[x, y] = px[y * n + x].r / 255f; // 白で描いたので、赤の濃さ＝字の濃さ
        return f;
    }

    // ---- 立体と骨 ----

    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector3> normals = new List<Vector3>();
    readonly List<BoneWeight> weights = new List<BoneWeight>();
    readonly List<int> tris = new List<int>();

    bool Build(float[,] f, Material material)
    {
        int n = f.GetLength(0);
        const float Th = 0.5f; // これより濃いところを字の中とする

        // 字のある範囲
        int minX = n, maxX = -1, minY = n, maxY = -1;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (f[x, y] >= Th)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < 0) return false;

        float cell = EmHeight / FontSize;
        float cx = (minX + maxX) * 0.5f;
        w = Mathf.Max((maxX - minX) * cell, cell);
        h = Mathf.Max((maxY - minY) * cell, cell);
        // 字の下の真ん中が原点になるようにする
        Vector2 P(float x, float y) => new Vector2((x - cx) * cell, (y - minY) * cell);

        // 表と裏は同じ点を使い回す（つなぎ目が割れないように）
        var front = new Dictionary<long, int>();
        var back = new Dictionary<long, int>();
        float zf = -Thickness * 0.5f, zb = Thickness * 0.5f;

        var poly = new List<Vector2>(8);
        var keys = new List<long>(8);
        var isEdge = new List<bool>(8);
        var cxs = new int[4]; var cys = new int[4]; var vs = new float[4];

        for (int y = 0; y < n - 1; y++)
            for (int x = 0; x < n - 1; x++)
            {
                // ます目の4つの角（反時計回り）
                cxs[0] = x;     cys[0] = y;
                cxs[1] = x + 1; cys[1] = y;
                cxs[2] = x + 1; cys[2] = y + 1;
                cxs[3] = x;     cys[3] = y + 1;
                bool any = false;
                for (int k = 0; k < 4; k++) { vs[k] = f[cxs[k], cys[k]]; any |= vs[k] >= Th; }
                if (!any) continue;

                // 角（字の中のもの）と、辺上の輪郭の点を、反時計回りに並べる
                poly.Clear(); keys.Clear(); isEdge.Clear();
                for (int k = 0; k < 4; k++)
                {
                    int j = (k + 1) & 3;
                    bool inA = vs[k] >= Th, inB = vs[j] >= Th;
                    if (inA)
                    {
                        poly.Add(P(cxs[k], cys[k]));
                        keys.Add(Key(2 * cxs[k], 2 * cys[k]));
                        isEdge.Add(false);
                    }
                    if (inA != inB)
                    {
                        float t = (Th - vs[k]) / (vs[j] - vs[k]);
                        poly.Add(P(Mathf.Lerp(cxs[k], cxs[j], t), Mathf.Lerp(cys[k], cys[j], t)));
                        keys.Add(Key(cxs[k] + cxs[j], cys[k] + cys[j]));
                        isEdge.Add(true);
                    }
                }
                int m = poly.Count;

                // 表と裏の面
                for (int i = 1; i < m - 1; i++)
                {
                    AddTri(Shared(front, keys[0], poly[0], zf), Shared(front, keys[i], poly[i], zf), Shared(front, keys[i + 1], poly[i + 1], zf), Vector3.back);
                    AddTri(Shared(back, keys[0], poly[0], zb), Shared(back, keys[i], poly[i], zb), Shared(back, keys[i + 1], poly[i + 1], zb), Vector3.forward);
                }

                // 横の壁: 輪郭の点が続くところが字のふち
                for (int i = 0; i < m; i++)
                {
                    int j = (i + 1) % m;
                    if (!isEdge[i] || !isEdge[j]) continue;
                    Vector2 a = poly[i], b = poly[j], d = b - a;
                    if (d.sqrMagnitude < 1e-12f) continue;
                    var nrm = new Vector3(d.y, -d.x, 0).normalized; // 反時計回りなので右側が外
                    int a0 = AddVert(new Vector3(a.x, a.y, zf), nrm);
                    int b0 = AddVert(new Vector3(b.x, b.y, zf), nrm);
                    int b1 = AddVert(new Vector3(b.x, b.y, zb), nrm);
                    int a1 = AddVert(new Vector3(a.x, a.y, zb), nrm);
                    AddTri(a0, b0, b1, nrm);
                    AddTri(a0, b1, a1, nrm);
                }
            }

        if (tris.Count == 0) return false;

        // 骨を置く（字を囲む箱の決まった位置。字の形は見ない）
        body = new GameObject("Body").transform;
        body.SetParent(transform, false);
        bones = new Transform[BoneCount];
        bones[Hips]  = MakeBone("Hips",  body,        new Vector3(0, 0.42f * h, 0));
        bones[LegL]  = MakeBone("LegL",  bones[Hips], new Vector3(-0.2f * w, 0.42f * h, 0));
        bones[LegR]  = MakeBone("LegR",  bones[Hips], new Vector3(0.2f * w, 0.42f * h, 0));
        bones[Chest] = MakeBone("Chest", bones[Hips], new Vector3(0, 0.6f * h, 0));
        bones[Head]  = MakeBone("Head",  bones[Chest], new Vector3(0, 0.8f * h, 0));
        bones[ArmL]  = MakeBone("ArmL",  bones[Chest], new Vector3(-0.22f * w, 0.65f * h, 0));
        bones[ArmR]  = MakeBone("ArmR",  bones[Chest], new Vector3(0.22f * w, 0.65f * h, 0));

        var bind = new Matrix4x4[BoneCount];
        for (int i = 0; i < BoneCount; i++)
            bind[i] = bones[i].worldToLocalMatrix * body.localToWorldMatrix;

        var mesh = new Mesh { name = "Kanji " + Letter };
        if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetTriangles(tris, 0);
        mesh.boneWeights = weights.ToArray();
        mesh.bindposes = bind;
        mesh.RecalculateBounds();

        var smr = body.gameObject.AddComponent<SkinnedMeshRenderer>();
        smr.sharedMesh = mesh;
        smr.bones = bones;
        smr.rootBone = bones[Hips];
        smr.sharedMaterial = material;
        smr.updateWhenOffscreen = true;
        return true;
    }

    static long Key(int x2, int y2) => (long)x2 * 100000 + y2;

    Transform MakeBone(string name, Transform parent, Vector3 bodyPos)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.position = body.TransformPoint(bodyPos);
        return t;
    }

    int Shared(Dictionary<long, int> map, long key, Vector2 p, float z)
    {
        if (map.TryGetValue(key, out int i)) return i;
        i = AddVert(new Vector3(p.x, p.y, z), z < 0 ? Vector3.back : Vector3.forward);
        map[key] = i;
        return i;
    }

    int AddVert(Vector3 p, Vector3 n)
    {
        verts.Add(p);
        normals.Add(n);
        weights.Add(Weigh(p.x, p.y));
        return verts.Count - 1;
    }

    // 三角形の表が n の向きになるように並びを直して足す
    void AddTri(int a, int b, int c, Vector3 n)
    {
        Vector3 cr = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
        if (cr.sqrMagnitude < 1e-14f) return;
        if (Vector3.Dot(cr, n) < 0) { int t = b; b = c; c = t; }
        tris.Add(a); tris.Add(b); tris.Add(c);
    }

    // 点の位置だけで、どの骨にどれだけくっつくかを決める
    readonly float[] wb = new float[BoneCount];
    BoneWeight Weigh(float x, float y)
    {
        float hy = y / h, hx = x / w;
        float leg = 1f - Smooth(0.30f, 0.46f, hy);        // 下のほう → 脚
        float up = 1f - leg;
        float sideL = Smooth(0.18f, 0.32f, -hx);           // 左の端 → 左腕
        float sideR = Smooth(0.18f, 0.32f, hx);            // 右の端 → 右腕
        float head = Smooth(0.70f, 0.85f, hy) * (1f - Mathf.Max(sideL, sideR)); // 上の真ん中 → 頭
        float tL = Mathf.Clamp01(0.5f - hx / 0.24f);       // 真ん中あたりは左右の脚を混ぜる
        float torso = up * Mathf.Max(0f, 1f - sideL - sideR) * (1f - head);
        float chest = Smooth(0.45f, 0.65f, hy);

        wb[LegL] = leg * tL;
        wb[LegR] = leg * (1f - tL);
        wb[ArmL] = up * sideL;
        wb[ArmR] = up * sideR;
        wb[Head] = up * head;
        wb[Hips] = torso * (1f - chest);
        wb[Chest] = torso * chest;

        // 大きい順に4つまで
        var bw = new BoneWeight();
        float total = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            int best = -1; float bv = 1e-4f;
            for (int i = 0; i < BoneCount; i++) if (wb[i] > bv) { bv = wb[i]; best = i; }
            if (best < 0) break;
            wb[best] = 0;
            total += bv;
            switch (slot)
            {
                case 0: bw.boneIndex0 = best; bw.weight0 = bv; break;
                case 1: bw.boneIndex1 = best; bw.weight1 = bv; break;
                case 2: bw.boneIndex2 = best; bw.weight2 = bv; break;
                case 3: bw.boneIndex3 = best; bw.weight3 = bv; break;
            }
        }
        if (total <= 0) { bw.boneIndex0 = Hips; bw.weight0 = 1; return bw; }
        bw.weight0 /= total; bw.weight1 /= total; bw.weight2 /= total; bw.weight3 /= total;
        return bw;
    }

    static float Smooth(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    // ---- 動く ----

    public void Jump()
    {
        if (!grounded) return;
        velocity.y = JumpSpeed;
        grounded = false;
    }

    void Update()
    {
        if (bones == null) return;
        float dt = Time.deltaTime;

        // 移動（字はいつもカメラのほうを向いたまま、カニ歩きのように進む）
        var input = Vector2.ClampMagnitude(MoveInput, 1f);
        var target = new Vector3(input.x, 0, input.y) * MaxSpeed;
        var hv = new Vector3(velocity.x, 0, velocity.z);
        hv = Vector3.MoveTowards(hv, target, Accel * dt);
        velocity.x = hv.x; velocity.z = hv.z;
        if (!grounded) velocity.y -= Gravity * dt;

        var pos = transform.position + velocity * dt;
        if (pos.y <= 0) { pos.y = 0; velocity.y = 0; grounded = true; }
        pos.x = Mathf.Clamp(pos.x, -Area, Area);
        pos.z = Mathf.Clamp(pos.z, -Area, Area);
        transform.position = pos;

        float spd = hv.magnitude;
        float move = Mathf.Clamp01(spd / MaxSpeed);
        if (spd > 0.05f) lastDir = hv / spd;
        phase += spd * dt * 2.2f / Mathf.Max(0.6f, h * 0.5f); // 背が低い字ほどちょこちょこ歩く
        float t = Time.time + seed;

        if (grounded)
        {
            // 脚を交互に振る
            float swing = Mathf.Sin(phase) * 40f * move;
            bones[LegL].localRotation = LegRot(lastDir, swing);
            bones[LegR].localRotation = LegRot(lastDir, -swing);
            // 腕はばたばた（少しでたらめに）
            float flap = (Mathf.Sin(phase * 2f + 1f) * 45f + Mathf.PerlinNoise(t * 3f, 0) * 30f - 15f) * move
                       + Mathf.Sin(t * 2f) * 6f;
            bones[ArmL].localRotation = Quaternion.Euler(0, 0, flap);
            bones[ArmR].localRotation = Quaternion.Euler(0, 0, -flap + Mathf.Sin(phase * 3f) * 10f * move);
            // 頭はぐらぐら
            bones[Head].localRotation = Quaternion.Euler(0, 0, Mathf.Sin(phase + 0.5f) * 12f * move + Mathf.Sin(t * 1.7f) * 4f);
            bones[Chest].localRotation = Quaternion.Euler(0, Mathf.Sin(phase) * 10f * move, 0);
            // 体は弾んで、左右によちよち揺れて、進む向きに前のめり
            body.localPosition = new Vector3(0, Mathf.Abs(Mathf.Sin(phase)) * 0.12f * move, 0);
            var lean = Quaternion.FromToRotation(Vector3.up, (Vector3.up + lastDir * 0.25f * move).normalized);
            body.localRotation = lean * Quaternion.Euler(0, 0, Mathf.Sin(phase) * 8f * move);
        }
        else
        {
            // 空中ではじたばた
            float kick = Mathf.Sin(t * 25f) * 35f;
            bones[LegL].localRotation = LegRot(lastDir, kick);
            bones[LegR].localRotation = LegRot(lastDir, -kick);
            bones[ArmL].localRotation = Quaternion.Euler(0, 0, -60f + Mathf.Sin(t * 30f) * 25f);
            bones[ArmR].localRotation = Quaternion.Euler(0, 0, 60f - Mathf.Sin(t * 30f + 1f) * 25f);
            bones[Head].localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 12f) * 10f);
            body.localPosition = Vector3.zero;
            body.localRotation = Quaternion.Slerp(body.localRotation, Quaternion.identity, 5f * dt);
        }
    }

    // 下向きの脚を、進む向き d のほうへ deg 度だけ振る
    static Quaternion LegRot(Vector3 d, float deg)
    {
        var target = (Vector3.down + d * Mathf.Tan(deg * Mathf.Deg2Rad)).normalized;
        return Quaternion.FromToRotation(Vector3.down, target);
    }
}
