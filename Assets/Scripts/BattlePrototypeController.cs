using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 3D painting-battle prototype. The controller can live in a scene or create itself
/// at runtime. Add Assets/Resources/PlayableMap.png (Read/Write Enabled) to replace
/// the demonstration image with a host-provided map.
/// </summary>
[ExecuteAlways]
public sealed class BattlePrototypeController : MonoBehaviour
{
    const int Width = 192;
    const int Height = 112;
    const float BoardWidth = 60f;
    const float BoardDepth = 36f;
    const float TotalSeconds = 75f;
    const float WallFallsAt = 30f;

    readonly Color32[] palette = {
        new Color32(255, 107, 107, 255), new Color32(255, 205, 86, 255),
        new Color32(70, 201, 130, 255), new Color32(70, 155, 255, 255),
        new Color32(172, 112, 255, 255)
    };

    Color32[] targets;
    Color32[] paint;
    Texture2D mapTexture;
    GameObject playerObject, rivalObject, wallObject;
    Transform mapRoot, playerRoot, rivalRoot, environmentRoot;
    Transform editorPreviewRoot;
    Transform paintStampRoot;
    Vector3 lastPlayerStamp, lastRivalStamp;
    bool hasPlayerStamp, hasRivalStamp;
    readonly List<LineRenderer> playerTrails = new List<LineRenderer>();
    LineRenderer activeTrail, activeRivalTrail;
    int activeTrailColor = -1, activeRivalTrailColor = -1;
    Transform cameraTransform;
    Vector3 player = new Vector3(-22f, 0f, -10f);
    Vector3 rival = new Vector3(22f, 0f, 10f);
    Vector3 rivalDestination = new Vector3(22f, 0f, 10f);
    float playerVelocityY, rivalTurn, remaining = TotalSeconds;
    float cameraYaw, cameraPitch = 38f;
    int selectedColor;
    bool wallDown, finished;
    readonly System.Random random = new System.Random(19);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void CreateIfMissing()
    {
        if (FindObjectOfType<BattlePrototypeController>() == null)
            new GameObject("Battle Prototype").AddComponent<BattlePrototypeController>();
    }

    void Awake()
    {
        if (!Application.isPlaying)
        {
            CreateEditorPreview();
            return;
        }
        Application.targetFrameRate = 60;
        SetupCamera();
        BuildMap(Resources.Load<Texture2D>("PlayableMap"));
        CreateWorldObjects();
    }

    void OnEnable()
    {
        if (!Application.isPlaying) CreateEditorPreview();
    }

    // These objects are deliberately editor-only scene previews. They let the designer
    // compose and inspect the battle space before entering Play mode.
    void CreateEditorPreview()
    {
        editorPreviewRoot = transform.Find("Editor Preview");
        if (editorPreviewRoot != null) return;
        editorPreviewRoot = new GameObject("Editor Preview").transform;
        editorPreviewRoot.SetParent(transform);

        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Map Surface Preview";
        floor.transform.SetParent(editorPreviewRoot);
        floor.transform.localScale = new Vector3(BoardWidth / 10f, 1f, BoardDepth / 10f);
        floor.GetComponent<Renderer>().material = NewColorMaterial(new Color(.06f, .13f, .22f));

        CreatePreviewActor("Player Preview", new Color(.12f, .55f, 1f), player);
        CreatePreviewActor("Rival Preview", new Color(1f, .2f, .3f), rival);

        GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "Center Barrier Preview";
        wall.transform.SetParent(editorPreviewRoot);
        wall.transform.position = new Vector3(0f, 1.8f, 0f);
        wall.transform.localScale = new Vector3(.3f, 3.6f, BoardDepth);
        wall.GetComponent<Renderer>().material = NewColorMaterial(new Color(.85f, .92f, 1f));
    }

    void CreatePreviewActor(string name, Color color, Vector3 position)
    {
        GameObject actor = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        actor.name = name;
        actor.transform.SetParent(editorPreviewRoot);
        actor.transform.position = position + Vector3.up;
        actor.GetComponent<Renderer>().material = NewColorMaterial(color);
    }

    void SetupCamera()
    {
        Camera camera = Camera.main;
        if (camera == null) camera = new GameObject("Battle Camera").AddComponent<Camera>();
        camera.orthographic = false;
        camera.fieldOfView = 66f;
        camera.backgroundColor = new Color(.035f, .05f, .085f);
        cameraTransform = camera.transform;
        cameraTransform.position = player + new Vector3(0f, 8.5f, -11f);
        cameraTransform.LookAt(player + new Vector3(0f, 1.2f, 4.5f));
    }

    /// <summary>Entry point for a future room-host upload UI.</summary>
    public void BuildMap(Texture2D uploadedImage)
    {
        targets = new Color32[Width * Height];
        paint = new Color32[targets.Length];
        Texture2D source = uploadedImage != null && uploadedImage.isReadable ? uploadedImage : CreateDemoImage();
        for (int z = 0; z < Height; z++)
        for (int x = 0; x < Width; x++)
        {
            Color sample = source.GetPixelBilinear((x + .5f) / Width, (z + .5f) / Height);
            targets[Index(x, z)] = palette[NearestPalette(sample)];
        }
        if (mapTexture == null)
        {
            mapTexture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
        }
        RefreshMapTexture();
    }

    Texture2D CreateDemoImage()
    {
        var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
        for (int z = 0; z < Height; z++)
        for (int x = 0; x < Width; x++)
        {
            float u = (x - Width * .5f) / Width;
            float v = (z - Height * .5f) / Height;
            int band = Mathf.FloorToInt((Mathf.Atan2(v, u) + Mathf.PI) / (Mathf.PI * 2f) * palette.Length + (u * u + v * v) * 4f) % palette.Length;
            texture.SetPixel(x, z, palette[band]);
        }
        texture.Apply();
        return texture;
    }

    void CreateWorldObjects()
    {
        editorPreviewRoot = transform.Find("Editor Preview");
        if (editorPreviewRoot != null) editorPreviewRoot.gameObject.SetActive(false);
        mapRoot = GetContainer("Map");
        playerRoot = GetContainer("Player");
        rivalRoot = GetContainer("Rival");
        environmentRoot = GetContainer("Environment");
        CreateFloor();
        paintStampRoot = GetContainer("Paint Stamps");
        playerObject = CreateActor("Body", new Color(.12f, .55f, 1f), player, playerRoot);
        rivalObject = CreateActor("Body", new Color(1f, .2f, .3f), rival, rivalRoot);
        wallObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wallObject.name = "Center Barrier (falls at 30 seconds)";
        wallObject.transform.SetParent(environmentRoot);
        wallObject.transform.position = new Vector3(0f, 1.25f, 0f);
        wallObject.transform.localScale = new Vector3(.3f, 3.6f, BoardDepth);
        wallObject.GetComponent<Renderer>().material.color = new Color(.85f, .92f, 1f);
    }

    void CreateFloor()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Playable Paint Map";
        floor.transform.SetParent(mapRoot);
        floor.transform.localScale = new Vector3(BoardWidth / 10f, 1f, BoardDepth / 10f);
        floor.GetComponent<Renderer>().material = NewMapMaterial();

        var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
        border.name = "Map Base";
        border.transform.SetParent(mapRoot);
        border.transform.position = new Vector3(0f, -.12f, 0f);
        border.transform.localScale = new Vector3(BoardWidth + .3f, .2f, BoardDepth + .3f);
        border.GetComponent<Renderer>().material.color = new Color(.06f, .08f, .13f);
    }

    Material NewMapMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Texture");
        var material = new Material(shader) { mainTexture = mapTexture };
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", mapTexture);
        return material;
    }

    Material NewColorMaterial(Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        var material = new Material(shader) { color = color };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        return material;
    }

    Transform GetContainer(string name)
    {
        Transform container = transform.Find(name);
        if (container != null) return container;
        var created = new GameObject(name).transform;
        created.SetParent(transform);
        return created;
    }

    GameObject CreateActor(string label, Color color, Vector3 position, Transform parent)
    {
        var actor = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        actor.name = label;
        actor.transform.SetParent(parent);
        actor.transform.position = position + Vector3.up;
        actor.GetComponent<Renderer>().material.color = color;
        return actor;
    }

    void Update()
    {
        if (!Application.isPlaying) return;
        if (finished) { if (Input.GetKeyDown(KeyCode.R)) Restart(); return; }
        remaining = Mathf.Max(0f, remaining - Time.deltaTime);
        if (!wallDown && remaining <= WallFallsAt) { wallDown = true; wallObject.SetActive(false); }
        MovePlayer();
        MoveRival();
        PaintAt(playerObject.transform.position, selectedColor);
        AddPaintStamp(playerObject.transform.position, selectedColor, ref hasPlayerStamp, ref lastPlayerStamp, "Player Paint");
        int rivalColor = NearestPalette(TargetAt(rival));
        PaintAt(rivalObject.transform.position, rivalColor);
        AddPaintStamp(rivalObject.transform.position, rivalColor, ref hasRivalStamp, ref lastRivalStamp, "Rival Paint");
        if (remaining <= 0f) finished = true;
    }

    void MovePlayer()
    {
        if (Input.GetKeyDown(KeyCode.Q)) selectedColor = (selectedColor + palette.Length - 1) % palette.Length;
        if (Input.GetKeyDown(KeyCode.E)) selectedColor = (selectedColor + 1) % palette.Length;
        Vector3 input = new Vector3((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0), 0f, (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0));
        if (input.sqrMagnitude > 1f) input.Normalize();
        Vector3 moveDirection = Quaternion.Euler(0f, cameraYaw, 0f) * input;
        player += moveDirection * 9f * Time.deltaTime;
        if (!wallDown) player.x = Mathf.Min(player.x, -.45f);
        if (Input.GetKeyDown(KeyCode.Space) && player.y <= 0.001f) playerVelocityY = 6.1f;
        playerVelocityY += Physics.gravity.y * Time.deltaTime;
        player.y = Mathf.Max(0f, player.y + playerVelocityY * Time.deltaTime);
        if (player.y <= 0f) playerVelocityY = 0f;
        player = ClampToBoard(player);
        playerObject.transform.position = player + Vector3.up;
    }

    void MoveRival()
    {
        rivalTurn -= Time.deltaTime;
        if (rivalTurn <= 0f)
        {
            rivalTurn = .6f + (float)random.NextDouble() * 1.2f;
            rivalDestination = ClampToBoard(rival + new Vector3(UnityEngine.Random.Range(-2.5f, 2.5f), 0f, UnityEngine.Random.Range(-2.5f, 2.5f)));
        }
        if (!wallDown) rivalDestination.x = Mathf.Max(rivalDestination.x, .45f);
        rival = Vector3.MoveTowards(rival, rivalDestination, 4.5f * Time.deltaTime);
        rivalObject.transform.position = rival + Vector3.up;
    }

    // Painting is intentionally independent of territory and the barrier.
    // Each actor always paints the ground directly below their current 3D position.
    void PaintAt(Vector3 world, int color)
    {
        int cx = Mathf.Clamp(Mathf.FloorToInt((world.x / BoardWidth + .5f) * Width), 0, Width - 1);
        int cz = Mathf.Clamp(Mathf.FloorToInt((world.z / BoardDepth + .5f) * Height), 0, Height - 1);
        for (int z = cz - 1; z <= cz + 1; z++)
        for (int x = cx - 1; x <= cx + 1; x++)
        {
            if (x < 0 || z < 0 || x >= Width || z >= Height) continue;
            paint[Index(x, z)] = palette[color];
        }
        // Visual paint is rendered as physical decals below each actor, not by changing
        // this map texture. This keeps each visible mark at its world-space position.
    }

    void AddPaintStamp(Vector3 world, int color, ref bool hasLastStamp, ref Vector3 lastStamp, string label)
    {
        Vector3 point = new Vector3(world.x, .025f, world.z);
        if (hasLastStamp && Vector3.Distance(lastStamp, point) < .42f) return;
        var stamp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        stamp.name = label;
        stamp.transform.SetParent(paintStampRoot);
        stamp.transform.position = point;
        stamp.transform.localScale = new Vector3(.72f, .012f, .72f);
        stamp.GetComponent<Renderer>().material = NewColorMaterial(palette[color]);
        Destroy(stamp.GetComponent<Collider>());
        hasLastStamp = true;
        lastStamp = point;
    }

    void AddPlayerTrail(Vector3 world)
    {
        AddTrail(world, selectedColor, ref activeTrail, ref activeTrailColor, "Player Paint Trail");
    }

    void AddRivalTrail(Vector3 world, int color)
    {
        AddTrail(world, color, ref activeRivalTrail, ref activeRivalTrailColor, "Rival Paint Trail");
    }

    void AddTrail(Vector3 world, int color, ref LineRenderer activeLine, ref int activeColor, string objectName)
    {
        Vector3 point = new Vector3(world.x, .055f, world.z);
        if (activeLine == null || activeColor != color)
        {
            var stroke = new GameObject(objectName);
            activeLine = stroke.AddComponent<LineRenderer>();
            activeLine.useWorldSpace = true;
            activeLine.positionCount = 0;
            activeLine.widthMultiplier = 1.15f;
            activeLine.numCapVertices = 6;
            activeLine.numCornerVertices = 6;
            activeLine.material = NewColorMaterial(palette[color]);
            activeColor = color;
            playerTrails.Add(activeLine);
        }
        int last = activeLine.positionCount - 1;
        if (last < 0 || Vector3.Distance(activeLine.GetPosition(last), point) >= .12f)
        {
            activeLine.positionCount++;
            activeLine.SetPosition(activeLine.positionCount - 1, point);
        }
    }

    // Shows the goal subtly and a team tint; painted pixels are fully saturated.
    void RefreshMapTexture()
    {
        var display = new Color32[targets.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            int x = i % Width;
            Color goal = targets[i];
            Color teamTint = TeamAt(x) == 0 ? new Color(.025f, .09f, .23f) : new Color(.23f, .025f, .06f);
            display[i] = paint[i].a > 0 ? paint[i] : Color.Lerp(goal * .16f, teamTint, .52f);
        }
        mapTexture.SetPixels32(display);
        mapTexture.Apply(false);
    }

    int Index(int x, int z) { return z * Width + x; }
    int TeamAt(int x) { return x < Width / 2 ? 0 : 1; }
    Color TargetAt(Vector3 world)
    {
        int x = Mathf.Clamp(Mathf.FloorToInt((world.x / BoardWidth + .5f) * Width), 0, Width - 1);
        int z = Mathf.Clamp(Mathf.FloorToInt((world.z / BoardDepth + .5f) * Height), 0, Height - 1);
        return targets[Index(x, z)];
    }

    int NearestPalette(Color color)
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            float dr = palette[i].r / 255f - color.r;
            float dg = palette[i].g / 255f - color.g;
            float db = palette[i].b / 255f - color.b;
            float distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    Vector3 ClampToBoard(Vector3 value)
    {
        value.x = Mathf.Clamp(value.x, -29.5f, 29.5f);
        value.z = Mathf.Clamp(value.z, -17.5f, 17.5f);
        return value;
    }

    float Score(int team)
    {
        int valid = 0, correct = 0;
        for (int z = 0; z < Height; z++)
        for (int x = 0; x < Width; x++)
        {
            if (TeamAt(x) != team) continue;
            valid++;
            Color32 p = paint[Index(x, z)], target = targets[Index(x, z)];
            if (p.a > 0 && p.r == target.r && p.g == target.g && p.b == target.b) correct++;
        }
        return 100f * correct / valid;
    }

    void Restart()
    {
        remaining = TotalSeconds; wallDown = finished = false; selectedColor = 0;
        player = new Vector3(-22f, 0f, -10f); rival = rivalDestination = new Vector3(22f, 0f, 10f);
        Array.Clear(paint, 0, paint.Length); wallObject.SetActive(true); RefreshMapTexture();
        foreach (Transform stamp in paintStampRoot) Destroy(stamp.gameObject);
        hasPlayerStamp = hasRivalStamp = false;
        foreach (LineRenderer trail in playerTrails) if (trail != null) Destroy(trail.gameObject);
        playerTrails.Clear(); activeTrail = activeRivalTrail = null;
        activeTrailColor = activeRivalTrailColor = -1;
    }

    void OnGUI()
    {
        if (!Application.isPlaying) return;
        GUIStyle title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        GUIStyle text = new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = new Color(.88f, .92f, 1f) } };
        GUI.Label(new Rect(24, 18, 550, 32), "COLOR CLASH  ·  3D BATTLE PROTOTYPE", title);
        GUI.Label(new Rect(24, 50, 600, 24), $"남은 시간 {Mathf.CeilToInt(remaining):00}s     내 정확도 {Score(0):0.0}%     상대 {Score(1):0.0}%", text);
        GUI.Label(new Rect(24, 74, 700, 24), wallDown ? "⚠ 장벽 붕괴 — 상대 구역을 방해할 수 있습니다" : "장벽 유지 중 — 내 구역을 올바른 색으로 칠하세요", text);
        GUI.Label(new Rect(24, Screen.height - 48, 900, 24), "WASD 이동 · 우클릭 드래그 시점 회전 · Q / E 색상 변경 · SPACE 점프 · R 다시 시작", text);
        Color old = GUI.color; GUI.color = palette[selectedColor]; GUI.Box(new Rect(Screen.width - 72, 24, 42, 42), GUIContent.none); GUI.color = old;
        GUI.Label(new Rect(Screen.width - 220, 72, 200, 22), $"선택 색상 {selectedColor + 1} / {palette.Length}", text);
        if (finished)
        {
            GUI.Box(new Rect(Screen.width / 2 - 170, Screen.height / 2 - 55, 340, 110), GUIContent.none);
            GUI.Label(new Rect(Screen.width / 2 - 130, Screen.height / 2 - 35, 280, 30), Score(0) >= Score(1) ? "승리!" : "패배", title);
            GUI.Label(new Rect(Screen.width / 2 - 130, Screen.height / 2, 300, 28), $"내 정확도 {Score(0):0.0}% · R 키로 재시작", text);
        }
    }

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        if (cameraTransform == null || playerObject == null) return;
        if (Input.GetMouseButton(1))
        {
            cameraYaw += Input.GetAxis("Mouse X") * 4.2f;
            cameraPitch = Mathf.Clamp(cameraPitch - Input.GetAxis("Mouse Y") * 3.2f, 18f, 72f);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        Quaternion rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0f);
        Vector3 desiredPosition = playerObject.transform.position + rotation * new Vector3(0f, 0f, -13.9f);
        cameraTransform.position = Vector3.Lerp(cameraTransform.position, desiredPosition, 7f * Time.deltaTime);
        Vector3 lookTarget = playerObject.transform.position + Vector3.up * 1.1f + Quaternion.Euler(0f, cameraYaw, 0f) * Vector3.forward * 4.2f;
        cameraTransform.LookAt(lookTarget);
    }
}
