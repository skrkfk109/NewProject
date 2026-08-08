using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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
    const float PaintRadius = .36f;
    const float TotalSeconds = 75f;
    const float WallFallsAt = 30f;

    readonly Color32[] palette = {
        new Color32(255, 107, 107, 255), new Color32(255, 205, 86, 255),
        new Color32(70, 201, 130, 255), new Color32(70, 155, 255, 255),
        new Color32(172, 112, 255, 255)
    };

    Color32[] targets;
    Color32[] paint;
    int[] paintOwner;
    Texture2D mapTexture;
    Texture2D paintTexture;
    Collider mapFloorCollider;
    GameObject playerObject, rivalObject, wallObject;
    CharacterController playerController;
    Transform mapRoot, playerRoot, rivalRoot, environmentRoot;
    Transform editorPreviewRoot;
    Transform paintStampRoot;
    Vector3 lastPlayerStamp, lastRivalStamp;
    bool hasPlayerStamp, hasRivalStamp;
    readonly List<LineRenderer> playerTrails = new List<LineRenderer>();
    LineRenderer activeTrail, activeRivalTrail;
    int activeTrailColor = -1, activeRivalTrailColor = -1;
    Transform cameraTransform, cameraTarget, cameraPivot;
    Vector3 player = new Vector3(-22f, 0f, -10f);
    Vector3 rival = new Vector3(22f, 0f, 10f);
    Vector3 rivalDestination = new Vector3(22f, 0f, 10f);
    float playerVelocityY, rivalTurn, remaining = TotalSeconds;
    float cameraYaw, cameraPitch = 38f;
    int selectedColor;
    bool wallDown, finished;
    bool runtimeInitialized;
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
        InitializeRuntime();
    }

    void OnEnable()
    {
        if (!Application.isPlaying) CreateEditorPreview();
    }

    void Start()
    {
        if (Application.isPlaying) InitializeRuntime();
    }

    void InitializeRuntime()
    {
        if (runtimeInitialized && playerObject != null && rivalObject != null) return;
        runtimeInitialized = true;
        Application.targetFrameRate = 60;
        SetupCamera();
        BuildMap(Resources.Load<Texture2D>("PlayableMap"));
        CreateWorldObjects();
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
        paintOwner = new int[targets.Length];
        for (int i = 0; i < paintOwner.Length; i++) paintOwner[i] = -1;
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
        playerController = playerObject.AddComponent<CharacterController>();
        playerController.center = Vector3.zero;
        playerController.height = 2f;
        playerController.radius = .48f;
        playerController.stepOffset = .35f;
        wallObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wallObject.name = "Center Barrier (falls at 30 seconds)";
        wallObject.transform.SetParent(environmentRoot);
        wallObject.transform.position = new Vector3(0f, 1.25f, 0f);
        wallObject.transform.localScale = new Vector3(.3f, 3.6f, BoardDepth);
        wallObject.GetComponent<Renderer>().material.color = new Color(.85f, .92f, 1f);
        CreateCameraRig();
    }

    void CreateFloor()
    {
        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Playable Paint Map";
        floor.transform.SetParent(mapRoot);
        floor.transform.localScale = new Vector3(BoardWidth / 10f, 1f, BoardDepth / 10f);
        floor.GetComponent<Renderer>().material = NewMapMaterial();
        mapFloorCollider = floor.GetComponent<Collider>();

        // This transparent overlay is the visible source of truth for paint. A later
        // colour write replaces the same texture cells, so paint can be overwritten.
        paintTexture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        var paintOverlay = GameObject.CreatePrimitive(PrimitiveType.Plane);
        paintOverlay.name = "Paint Overlay";
        paintOverlay.transform.SetParent(mapRoot);
        paintOverlay.transform.position = new Vector3(0f, .015f, 0f);
        paintOverlay.transform.localScale = new Vector3(BoardWidth / 10f, 1f, BoardDepth / 10f);
        Material overlayMaterial = NewTransparentTextureMaterial(paintTexture);
        overlayMaterial.renderQueue = (int)RenderQueue.Transparent + 1;
        paintOverlay.GetComponent<Renderer>().material = overlayMaterial;
        Destroy(paintOverlay.GetComponent<Collider>());

        var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
        border.name = "Map Base";
        border.transform.SetParent(mapRoot);
        border.transform.position = new Vector3(0f, -.12f, 0f);
        border.transform.localScale = new Vector3(BoardWidth + .3f, .2f, BoardDepth + .3f);
        border.GetComponent<Renderer>().material.color = new Color(.06f, .08f, .13f);
    }

    Material NewMapMaterial()
    {
        return NewTransparentTextureMaterial(mapTexture);
    }

    Material NewTransparentTextureMaterial(Texture2D texture)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Texture");
        var material = new Material(shader) { mainTexture = texture };
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
        material.renderQueue = (int)RenderQueue.Transparent;
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
        actor.layer = LayerMask.NameToLayer("Ignore Raycast");
        Destroy(actor.GetComponent<CapsuleCollider>());
        actor.GetComponent<Renderer>().material.color = color;
        return actor;
    }

    void CreateCameraRig()
    {
        cameraTarget = new GameObject("Camera Target").transform;
        cameraPivot = new GameObject("Camera Pivot").transform;
        cameraTarget.SetParent(playerRoot);
        cameraPivot.SetParent(cameraTarget);
        cameraTarget.position = playerObject.transform.position + Vector3.up * .7f;
        cameraPivot.localPosition = Vector3.zero;
        cameraTransform.SetParent(cameraPivot);
        cameraTransform.localPosition = new Vector3(0f, 0f, -13.9f);
        cameraTransform.localRotation = Quaternion.identity;
    }

    void Update()
    {
        if (!Application.isPlaying) return;
        InitializeRuntime();
        if (playerObject == null || rivalObject == null) return;
        if (finished) { if (Input.GetKeyDown(KeyCode.R)) Restart(); return; }
        remaining = Mathf.Max(0f, remaining - Time.deltaTime);
        if (!wallDown && remaining <= WallFallsAt) { wallDown = true; wallObject.SetActive(false); }
        MovePlayer();
        MoveRival();
        // Painting is deliberate: movement alone never changes the map.
        if (Input.GetMouseButton(0) && TryGetMapHit(playerObject.transform, out RaycastHit playerHit))
        {
            if (AddPaintStamp(playerHit.point, selectedColor, ref hasPlayerStamp, ref lastPlayerStamp, "Player Paint"))
                PaintAt(playerHit.textureCoord, selectedColor, 0);
        }
        if (TryGetMapHit(rivalObject.transform, out RaycastHit rivalHit))
        {
            int rivalColor = TargetPaletteAt(rivalHit.textureCoord);
            if (AddPaintStamp(rivalHit.point, rivalColor, ref hasRivalStamp, ref lastRivalStamp, "Rival Paint"))
                PaintAt(rivalHit.textureCoord, rivalColor, 1);
        }
        if (remaining <= 0f) finished = true;
    }

    void MovePlayer()
    {
        if (Input.GetKeyDown(KeyCode.Q)) selectedColor = (selectedColor + palette.Length - 1) % palette.Length;
        if (Input.GetKeyDown(KeyCode.E)) selectedColor = (selectedColor + 1) % palette.Length;
        Vector3 input = new Vector3((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0), 0f, (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0));
        if (input.sqrMagnitude > 1f) input.Normalize();
        Vector3 moveDirection = Quaternion.Euler(0f, cameraYaw, 0f) * input;
        if (playerController.isGrounded && playerVelocityY < 0f) playerVelocityY = -1.5f;
        if (Input.GetKeyDown(KeyCode.Space) && playerController.isGrounded) playerVelocityY = 6.1f;
        playerVelocityY += Physics.gravity.y * Time.deltaTime;
        playerController.Move((moveDirection * 9f + Vector3.up * playerVelocityY) * Time.deltaTime);
        Vector3 constrained = playerObject.transform.position;
        constrained.x = Mathf.Clamp(constrained.x, -29.5f, wallDown ? 29.5f : -.45f);
        constrained.z = Mathf.Clamp(constrained.z, -17.5f, 17.5f);
        playerObject.transform.position = constrained;
        player = playerObject.transform.position - Vector3.up;
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
    void PaintAt(Vector2 mapUv, int color, int owner)
    {
        int cx = Mathf.Clamp(Mathf.FloorToInt(mapUv.x * Width), 0, Width - 1);
        int cz = Mathf.Clamp(Mathf.FloorToInt(mapUv.y * Height), 0, Height - 1);
        int radiusX = Mathf.CeilToInt(PaintRadius / (BoardWidth / Width));
        int radiusZ = Mathf.CeilToInt(PaintRadius / (BoardDepth / Height));
        for (int z = cz - radiusZ; z <= cz + radiusZ; z++)
        for (int x = cx - radiusX; x <= cx + radiusX; x++)
        {
            if (x < 0 || z < 0 || x >= Width || z >= Height) continue;
            float dx = ((x + .5f) / Width - mapUv.x) * BoardWidth;
            float dz = ((z + .5f) / Height - mapUv.y) * BoardDepth;
            if (dx * dx + dz * dz > PaintRadius * PaintRadius) continue;
            int index = Index(x, z);
            paint[index] = palette[color];
            paintOwner[index] = owner;
        }
        RefreshPaintTexture();
    }

    void RefreshPaintTexture()
    {
        if (paintTexture == null) return;
        paintTexture.SetPixels32(paint);
        paintTexture.Apply(false);
    }

    bool TryGetMapHit(Transform actor, out RaycastHit hit)
    {
        Vector3 origin = actor.position + Vector3.up * 2.5f;
        return Physics.Raycast(origin, Vector3.down, out hit, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && hit.collider == mapFloorCollider;
    }

    int TargetPaletteAt(Vector2 mapUv)
    {
        return NearestPalette(mapTexture.GetPixelBilinear(mapUv.x, mapUv.y));
    }

    bool AddPaintStamp(Vector3 world, int color, ref bool hasLastStamp, ref Vector3 lastStamp, string label)
    {
        Vector3 point = new Vector3(world.x, .025f, world.z);
        if (hasLastStamp && Vector3.Distance(lastStamp, point) < .42f) return false;
        hasLastStamp = true;
        lastStamp = point;
        return true;
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

    // Goal colours must stay true to the palette so players can identify the required
    // colour by sight. Team ownership is conveyed by the barrier and score UI instead.
    void RefreshMapTexture()
    {
        var display = new Color32[targets.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            // Keep the target hue easy to read while leaving enough brightness contrast
            // for the full-strength 3D paint stamps on top of it.
            Color32 faded = (Color)targets[i] * .78f;
            faded.a = 110;
            display[i] = faded;
        }
        mapTexture.SetPixels32(display);
        mapTexture.Apply(false);
    }

    int Index(int x, int z) { return z * Width + x; }
    // Unity's Plane primitive mirrors the texture's U axis relative to this scene's
    // world X axis. Blue starts on world-left, which is the high-U half of the map.
    int TeamAt(int x) { return x >= Width / 2 ? 0 : 1; }
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
        GetCoverage(team, out int total, out _, out int correct, out _, out _);
        return 100f * correct / total;
    }

    void GetCoverage(int team, out int total, out int paintedCells, out int correctCells, out int wrongCells, out int foreignCells)
    {
        total = paintedCells = correctCells = wrongCells = foreignCells = 0;
        for (int z = 0; z < Height; z++)
        for (int x = 0; x < Width; x++)
        {
            if (TeamAt(x) != team) continue;
            total++;
            int index = Index(x, z);
            if (paint[index].a == 0) continue;
            paintedCells++;
            Color32 p = paint[index], target = targets[index];
            if (p.r == target.r && p.g == target.g && p.b == target.b) correctCells++;
            else wrongCells++;
            if (paintOwner[index] != team) foreignCells++;
        }
    }

    void Restart()
    {
        remaining = TotalSeconds; wallDown = finished = false; selectedColor = 0;
        player = new Vector3(-22f, 0f, -10f); rival = rivalDestination = new Vector3(22f, 0f, 10f);
        Array.Clear(paint, 0, paint.Length);
        for (int i = 0; i < paintOwner.Length; i++) paintOwner[i] = -1;
        wallObject.SetActive(true); RefreshMapTexture(); RefreshPaintTexture();
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
        GUIStyle small = new GUIStyle(text) { fontSize = 12 };
        int standingIndex = CurrentGroundPaletteIndex();
        Color standingOn = standingIndex >= 0 ? palette[standingIndex] : Color.gray;
        float blueScore = Score(0), redScore = Score(1);

        GetCoverage(0, out int blueTotal, out int bluePainted, out int blueCorrect, out int blueWrong, out int blueForeign);
        GetCoverage(1, out int redTotal, out int redPainted, out int redCorrect, out int redWrong, out int redForeign);
        GUI.Box(new Rect(16, 12, 680, 136), GUIContent.none);
        GUI.Label(new Rect(24, 18, 550, 32), "COLOR CLASH  ·  3D BATTLE PROTOTYPE", title);
        GUI.Label(new Rect(24, 50, 130, 24), $"남은 시간 {Mathf.CeilToInt(remaining):00}s", text);
        GUI.Label(new Rect(172, 50, 170, 24), $"BLUE TEAM  {blueScore:0.0}%", text);
        GUI.Label(new Rect(350, 50, 160, 24), $"RED TEAM  {redScore:0.0}%", text);
        GUI.Label(new Rect(24, 77, 108, 20), $"현재 위치: {PaletteName(standingIndex)}", small);
        DrawColourSwatch(new Rect(136, 74, 30, 24), standingOn);
        GUI.Label(new Rect(190, 77, 68, 20), $"선택: {PaletteName(selectedColor)}", small);
        DrawColourSwatch(new Rect(262, 74, 30, 24), palette[selectedColor]);
        GUI.Label(new Rect(310, 77, 370, 20), wallDown ? "장벽 붕괴 — 자유 이동" : "장벽 유지 중", small);
        GUI.Label(new Rect(24, 105, 640, 18), $"[점수 검증] BLUE  칠함 {100f * bluePainted / blueTotal:0.0}% · 정답 {100f * blueCorrect / blueTotal:0.0}% · 오답 {100f * blueWrong / blueTotal:0.0}% · 상대흔적 {blueForeign}", small);
        GUI.Label(new Rect(24, 126, 640, 18), $"[점수 검증] RED     칠함 {100f * redPainted / redTotal:0.0}% · 정답 {100f * redCorrect / redTotal:0.0}% · 오답 {100f * redWrong / redTotal:0.0}% · 상대흔적 {redForeign}", small);
        GUI.Label(new Rect(24, Screen.height - 48, 920, 24), "WASD 이동 · 좌클릭 누르고 이동하면 색칠 · 우클릭 드래그 시점 회전 · Q / E 색상 변경 · SPACE 점프", text);
        if (finished)
        {
            GUI.Box(new Rect(Screen.width / 2 - 170, Screen.height / 2 - 55, 340, 110), GUIContent.none);
            GUI.Label(new Rect(Screen.width / 2 - 130, Screen.height / 2 - 35, 280, 30), Score(0) >= Score(1) ? "승리!" : "패배", title);
            GUI.Label(new Rect(Screen.width / 2 - 130, Screen.height / 2, 300, 28), $"내 정확도 {Score(0):0.0}% · R 키로 재시작", text);
        }
    }

    int CurrentGroundPaletteIndex()
    {
        if (playerObject == null || mapTexture == null) return -1;
        if (TryGetMapHit(playerObject.transform, out RaycastHit hit))
            return TargetPaletteAt(hit.textureCoord);
        return -1;
    }

    string PaletteName(int index)
    {
        string[] names = { "빨강", "노랑", "초록", "파랑", "보라" };
        return index >= 0 && index < names.Length ? names[index] : "없음";
    }

    void DrawColourSwatch(Rect rect, Color colour)
    {
        Color previous = GUI.color;
        GUI.color = new Color(.08f, .08f, .08f, 1f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = colour;
        GUI.DrawTexture(new Rect(rect.x + 3f, rect.y + 3f, rect.width - 6f, rect.height - 6f), Texture2D.whiteTexture);
        GUI.color = previous;
    }

    void LateUpdate()
    {
        if (!Application.isPlaying) return;
        if (cameraTransform == null || cameraTarget == null || cameraPivot == null || playerObject == null) return;
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
        cameraTarget.position = Vector3.Lerp(cameraTarget.position, playerObject.transform.position + Vector3.up * .7f, 12f * Time.deltaTime);
        cameraPivot.rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0f);
        float distance = 13.9f;
        if (Physics.SphereCast(cameraPivot.position, .25f, -cameraPivot.forward, out RaycastHit hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(2.5f, hit.distance - .2f);
        cameraTransform.localPosition = Vector3.Lerp(cameraTransform.localPosition, new Vector3(0f, 0f, -distance), 16f * Time.deltaTime);
        cameraTransform.localRotation = Quaternion.identity;
    }
}
