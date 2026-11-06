using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using ETouch = UnityEngine.InputSystem.EnhancedTouch;

// 漢字人間ゲームの画面と操作。シーンを再生すると自動で起動する（シーンに置く必要なし）。
//  PC: WASD / 矢印キーで歩く、スペースでジャンプ、上の欄に字を入れて「へんしん」
//  スマホ（ブラウザ）: 画面の左側をなぞって歩く、右下のボタンでジャンプ、上のボタンで字を変える
public class KanjiGame : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindAnyObjectByType<KanjiGame>() == null)
            new GameObject("KanjiGame").AddComponent<KanjiGame>();
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    // ブラウザの入力欄（日本語入力が使える）。Assets/Plugins/WebGL/KanjiInput.jslib
    [DllImport("__Internal")] static extern string KanjiPrompt(string message);
#endif

    Font font;
    Material baseMat;       // URP の標準の材質（これをコピーして色を変える）
    KanjiBody kanji;
    Camera cam;

    string inputText = "大";
    string message = "漢字を1文字入れてね";

    // スマホのスティック
    int stickTouch = -1;
    Vector2 stickStart, stickNow;
    float buttonCooldown;   // 1回のタップで2回押されないように

    void Start()
    {
        var fonts = Resources.LoadAll<Font>("Fonts");
        if (fonts.Length == 0) { message = "Assets/Resources/Fonts にフォントがありません"; return; }
        font = fonts[0];

        ETouch.EnhancedTouchSupport.Enable();

        // URP の標準の材質を、仮の立方体から借りる（ビルドで消されない材質）
        var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        baseMat = tmp.GetComponent<Renderer>().sharedMaterial;
        Destroy(tmp);

        SetupWorld();
        Spawn('大');
    }

    void SetupWorld()
    {
        // 床（市松模様で、歩いているのがわかるように）
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.localScale = new Vector3(6, 1, 6); // 60m 四方
        var check = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
        Color a = new Color(0.93f, 0.9f, 0.82f), b = new Color(0.82f, 0.86f, 0.78f);
        check.SetPixels(new[] { a, b, b, a });
        check.Apply();
        var fm = new Material(baseMat);
        fm.SetTexture("_BaseMap", check);
        fm.SetTextureScale("_BaseMap", new Vector2(30, 30));
        fm.SetColor("_BaseColor", Color.white);
        floor.GetComponent<Renderer>().sharedMaterial = fm;

        // 光（シーンに無ければ作る）
        if (FindAnyObjectByType<Light>() == null)
        {
            var l = new GameObject("Sun").AddComponent<Light>();
            l.type = LightType.Directional;
            l.shadows = LightShadows.Soft;
            l.transform.rotation = Quaternion.Euler(50, -30, 0);
        }

        cam = Camera.main;
        if (cam == null)
        {
            cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
        }
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.6f, 0.8f, 0.95f);
        cam.transform.position = new Vector3(0, 3.5f, -7.5f);
        cam.transform.LookAt(new Vector3(0, 1, 0));
    }

    void Spawn(char c)
    {
        var mat = new Material(baseMat);
        mat.SetColor("_BaseColor", Color.HSVToRGB(Random.value, 0.65f, 0.95f));
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.35f);

        var kb = KanjiBody.Create(c, font, mat, out string error);
        if (kb == null)
        {
            message = "「" + c + "」は作れなかった（" + error + "）";
            Destroy(mat);
            return;
        }
        Vector3 pos = Vector3.zero;
        if (kanji != null) { pos = kanji.transform.position; pos.y = 0; Destroy(kanji.gameObject); }
        kanji = kb;
        kanji.transform.position = pos;
        message = "「" + c + "」が歩くよ";
    }

    void ChangeLetter(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        char c = text[0];
        if (char.IsSurrogate(c)) { message = "その字は使えない"; return; }
        inputText = c.ToString();
        Spawn(c);
    }

    void AskInBrowser()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        ChangeLetter(KanjiPrompt("漢字を1文字入れてね"));
#endif
        buttonCooldown = Time.realtimeSinceStartup + 0.5f; // 入力欄を閉じた直後のタップを無視
    }

    // ---- 毎フレーム ----

    void Update()
    {
        if (kanji == null) return;

        Vector2 move = Vector2.zero;
        var kb = Keyboard.current;
        if (kb != null && !typing)
        {
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += 1;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= 1;
            if (kb.spaceKey.wasPressedThisFrame) kanji.Jump();
        }

        // タッチ: ボタン、または左側をなぞってスティック
        foreach (var t in ETouch.Touch.activeTouches)
        {
            Vector2 p = t.screenPosition;
            if (t.phase == UnityEngine.InputSystem.TouchPhase.Began)
            {
                if (PressButton(p)) continue;
                if (stickTouch < 0 && p.x < Screen.width * 0.6f)
                {
                    stickTouch = t.touchId; stickStart = stickNow = p;
                }
            }
            if (t.touchId == stickTouch)
            {
                stickNow = p;
                if (t.phase == UnityEngine.InputSystem.TouchPhase.Ended || t.phase == UnityEngine.InputSystem.TouchPhase.Canceled)
                    stickTouch = -1;
            }
        }
        if (stickTouch >= 0)
        {
            float r = Unit * 1.2f;
            move += Vector2.ClampMagnitude((stickNow - stickStart) / r, 1f);
        }

        // マウス（ブラウザの PC 版でもボタンを押せるように）
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && ETouch.Touch.activeTouches.Count == 0)
            PressButton(mouse.position.ReadValue());

        kanji.MoveInput = move;
    }

    void LateUpdate()
    {
        if (kanji == null || cam == null) return;
        // カメラは向きを変えずについていく
        var target = kanji.transform.position + new Vector3(0, 1, 0);
        var want = target + new Vector3(0, 2.5f, -7.5f);
        cam.transform.position = Vector3.Lerp(cam.transform.position, want, 1f - Mathf.Exp(-4f * Time.deltaTime));
        cam.transform.LookAt(cam.transform.position + (target - want));
    }

    // ---- 画面の部品 ----

    float Unit => Mathf.Min(Screen.width, Screen.height) / 12f;

    // GUI の座標（左上が原点）
    Rect JumpRect => new Rect(Screen.width - Unit * 2.6f, Screen.height - Unit * 2.6f, Unit * 2.1f, Unit * 2.1f);
    Rect ChangeRect => new Rect(Screen.width * 0.5f - Unit * 2.5f, Unit * 0.3f, Unit * 5f, Unit * 1.1f);

    bool typing; // PC で入力欄に文字を入れている最中

    // 画面座標（左下が原点）で押されたボタンを処理する。押したら true
    bool PressButton(Vector2 screenPos)
    {
        if (Time.realtimeSinceStartup < buttonCooldown) return false;
        var gp = new Vector2(screenPos.x, Screen.height - screenPos.y);
        if (JumpRect.Contains(gp)) { if (kanji != null) kanji.Jump(); return true; }
#if UNITY_WEBGL && !UNITY_EDITOR
        if (ChangeRect.Contains(gp)) { AskInBrowser(); return true; }
#endif
        return false;
    }

    GUIStyle label, box, field, button;

    void OnGUI()
    {
        float u = Unit;
        if (label == null && font != null)
        {
            label = new GUIStyle(GUI.skin.label) { font = font, alignment = TextAnchor.MiddleCenter };
            box = new GUIStyle(GUI.skin.box) { font = font, alignment = TextAnchor.MiddleCenter };
            field = new GUIStyle(GUI.skin.textField) { font = font, alignment = TextAnchor.MiddleCenter };
            button = new GUIStyle(GUI.skin.button) { font = font };
        }
        if (label == null) { GUI.Label(new Rect(10, 10, 600, 40), message); return; }
        label.fontSize = box.fontSize = field.fontSize = button.fontSize = Mathf.RoundToInt(u * 0.45f);
        label.normal.textColor = new Color(0.15f, 0.15f, 0.2f);

        var top = ChangeRect;
#if UNITY_WEBGL && !UNITY_EDITOR
        GUI.Box(top, "字をかえる", box);
#else
        // PC: 入力欄 ＋ へんしんボタン（Enter でも決定）
        var fr = new Rect(top.x, top.y, top.width * 0.55f, top.height);
        var br = new Rect(top.x + top.width * 0.6f, top.y, top.width * 0.4f, top.height);
        GUI.SetNextControlName("KanjiField");
        bool enter = Event.current.type == EventType.KeyDown &&
                     (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                     GUI.GetNameOfFocusedControl() == "KanjiField";
        inputText = GUI.TextField(fr, inputText, 8, field);
        typing = GUI.GetNameOfFocusedControl() == "KanjiField";
        if (GUI.Button(br, "へんしん", button) || enter)
        {
            ChangeLetter(inputText);
            GUI.FocusControl(null);
        }
#endif
        GUI.Label(new Rect(0, top.yMax + u * 0.1f, Screen.width, u * 0.9f), message, label);

        // ジャンプボタン（タッチ・マウスの判定は PressButton でしている）
        GUI.Box(JumpRect, "ジャンプ", box);

        // スティックの印
        if (stickTouch >= 0)
        {
            var s = new Vector2(stickStart.x, Screen.height - stickStart.y);
            var n = new Vector2(stickNow.x, Screen.height - stickNow.y);
            float r = u * 1.2f;
            GUI.Box(new Rect(s.x - r, s.y - r, r * 2, r * 2), "", box);
            var d = Vector2.ClampMagnitude(n - s, r);
            GUI.Box(new Rect(s.x + d.x - u * 0.5f, s.y + d.y - u * 0.5f, u, u), "", box);
        }
    }
}
