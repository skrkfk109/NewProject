using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 3D painting-battle prototype. The controller can live in a scene or create itself
/// at runtime. Assign any image to Map Source in the Inspector to use it as the
/// playable field. Read/Write is enabled automatically in the Unity editor.
/// </summary>
[ExecuteAlways]
public sealed class BattlePrototypeController : MonoBehaviour
{
    const int Width = 384;
    const int Height = 384;
    const float PaintRadius = .36f;
    const float ThirdPersonDistance = 8f;
    const float ThirdPersonTargetHeight = .95f;

    [Header("Camera Controls")]
    [SerializeField, Range(.1f, 12f)] float horizontalLookSensitivity = 4.2f;
    [SerializeField, Range(.1f, 12f)] float verticalLookSensitivity = 3.2f;
    [SerializeField, Range(2f, 12f)] float zoomInDistance = 3.5f;
    [SerializeField, Range(6f, 40f)] float zoomOutDistance = 18f;
    [SerializeField, Range(1f, 30f)] float zoomSensitivity = 12f;
    [Header("Image Field Generation")]
    [SerializeField, Range(3, 8)] int paletteSize = 5;
    [SerializeField, Range(24, 128)] int terrainColumns = 80;
    [SerializeField, Range(16, 96)] int terrainRows = 48;
    [SerializeField, Range(.1f, 12f)] float terrainHeight = 5f;
    [SerializeField, Range(.25f, 4f)] float heightContrast = 1.35f;
    [SerializeField, Range(0, 6)] int heightSmoothingPasses = 2;
    [SerializeField, Range(60f, 300f)] float fieldLongSide = 180f;
    [SerializeField] Texture2D mapSource;
    [SerializeField] GameObject playerModel;
    [SerializeField] Vector3 playerModelRotation = new Vector3(-90f, 0f, 0f);
    [SerializeField, Range(1f, 30f)] float playerTurnSpeed = 12f;

    float boardWidth = 60f;
    float boardDepth = 36f;
    float BoardWidth => boardWidth;
    float BoardDepth => boardDepth;
    const float TotalSeconds = 75f;
    const float WallFallsAt = 30f;

    Color32[] palette = {
        new Color32(255, 107, 107, 255), new Color32(255, 205, 86, 255),
        new Color32(70, 201, 130, 255), new Color32(70, 155, 255, 255),
        new Color32(172, 112, 255, 255)
    };

    Color32[] targets;
    Color32[] paint;
    int[] paintOwner;
    readonly int[] coverageTotal = new int[2];
    readonly int[] coveragePainted = new int[2];
    readonly int[] coverageCorrect = new int[2];
    readonly int[] coverageWrong = new int[2];
    readonly int[] coverageForeign = new int[2];
    bool coverageDirty = true;
    Texture2D mapTexture;
    Texture2D paintTexture;
    Texture2D sourceTexture;
    float[] terrainHeights;
    Mesh terrainMesh;
    Collider mapFloorCollider;
    GameObject terrainObject, paintOverlayObject;
    GameObject playerObject, rivalObject, wallObject;
    Transform playerVisualPivot, rivalVisualPivot;
    Vector3 playerVisualBasePosition, rivalVisualBasePosition;
    bool rivalIsMoving;
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
    float cameraYaw, cameraPitch = 16f;
    float desiredCameraDistance = ThirdPersonDistance;
    int selectedColor;
    bool wallDown, finished;
    bool multiplayerActive, multiplayerHost;
    int multiplayerTeam;
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
#if UNITY_EDITOR
        ResolvePlayerModelInEditor();
#endif
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

    void OnValidate()
    {
#if UNITY_EDITOR
        ResolvePlayerModelInEditor();
        // BuildMap samples image pixels. Enable this automatically so an image dragged
        // into the Inspector is not silently replaced by the demo map at Play time.
        if (mapSource != null)
        {
            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(mapSource);
            var importer = UnityEditor.AssetImporter.GetAtPath(assetPath) as UnityEditor.TextureImporter;
            if (importer != null && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }
#endif
    }

#if UNITY_EDITOR
    void ResolvePlayerModelInEditor()
    {
        const string modelPath = "Assets/Models/Slime_Model/tripo_convert_5530513a-4a0d-4c3e-9bae-71881d2ffa4a.fbx";
        GameObject importedModel = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (importedModel != null && playerModel != importedModel)
        {
            playerModel = importedModel;
            UnityEditor.EditorUtility.SetDirty(this);
        }
    }
#endif

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
        BuildMap(mapSource != null ? mapSource : Resources.Load<Texture2D>("PlayableMap"));
        CreateWorldObjects();
    }

    // These objects are deliberately editor-only scene previews. They let the designer
    // compose and inspect the battle space before entering Play mode.
    void CreateEditorPreview()
    {
        CreateUiHierarchy();
        editorPreviewRoot = transform.Find("Editor Preview");
        EnsurePlayerModelPreview();
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

    void EnsurePlayerModelPreview()
    {
        if (playerModel == null) return;
        Transform container = transform.Find("Player");
        if (container == null) return;
        Transform preview = container.Find("Slime Model Preview");
        if (preview == null)
        {
            GameObject previewObject = Instantiate(playerModel, container);
            previewObject.name = "Slime Model Preview";
            preview = previewObject.transform;
        }
        preview.localPosition = Vector3.zero;
        preview.localRotation = Quaternion.Euler(playerModelRotation);
        preview.localScale = Vector3.one;
        NormalizeModel(preview, 2f);
    }

    void CreateUiHierarchy()
    {
        Transform ui = transform.Find("UI");
        if (ui == null)
        {
            ui = new GameObject("UI").transform;
            ui.SetParent(transform);
        }
        CreateUiSlot(ui, "HUD");
        Transform hud = ui.Find("HUD");
        CreateUiSlot(hud, "Timer");
        CreateUiSlot(hud, "Team Scores");
        CreateUiSlot(hud, "Current Target Colour");
        CreateUiSlot(hud, "Selected Paint Colour");
        CreateUiSlot(hud, "Barrier Status");
        CreateUiSlot(hud, "Debug Score Readout");
        CreateUiSlot(ui, "Controls Hint");
        CreateUiSlot(ui, "End Result");
    }

    void CreateUiSlot(Transform parent, string label)
    {
        if (parent.Find(label) != null) return;
        var slot = new GameObject(label).transform;
        slot.SetParent(parent);
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
        camera.fieldOfView = 70f;
        camera.backgroundColor = new Color(.035f, .05f, .085f);
        cameraTransform = camera.transform;
        cameraTransform.position = player + new Vector3(0f, 3.2f, -7.4f);
        cameraTransform.LookAt(player + new Vector3(0f, ThirdPersonTargetHeight, 4f));
    }

    /// <summary>Entry point for a future room-host upload UI.</summary>
    public void BuildMap(Texture2D uploadedImage)
    {
        targets = new Color32[Width * Height];
        paint = new Color32[targets.Length];
        paintOwner = new int[targets.Length];
        for (int i = 0; i < paintOwner.Length; i++) paintOwner[i] = -1;
        coverageDirty = true;
        sourceTexture = uploadedImage != null ? MakeReadable(uploadedImage) : CreateDemoImage();
        SetFieldDimensions(sourceTexture);
        BuildPalette(sourceTexture);
        for (int z = 0; z < Height; z++)
        for (int x = 0; x < Width; x++)
        {
            Color sample = sourceTexture.GetPixelBilinear((x + .5f) / Width, (z + .5f) / Height);
            targets[Index(x, z)] = palette[NearestPalette(sample)];
        }
        BuildHeightMap(sourceTexture);
        if (mapTexture == null)
        {
            mapTexture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }
        RefreshMapTexture();
        if (terrainObject != null) RebuildTerrainGeometry();
    }

    Texture2D MakeReadable(Texture2D source)
    {
        if (source.isReadable) return source;

        // Also supports images supplied at runtime, where importer settings cannot be
        // changed. The copy is only created when Unity marks the source unreadable.
        RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        Graphics.Blit(source, temporary);
        RenderTexture.active = temporary;
        var readableCopy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false)
        {
            name = source.name + " (Runtime Readable Copy)",
            filterMode = source.filterMode,
            wrapMode = TextureWrapMode.Clamp
        };
        readableCopy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
        readableCopy.Apply(false);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(temporary);
        return readableCopy;
    }

    void SetFieldDimensions(Texture2D source)
    {
        float aspect = source.width / (float)source.height;
        if (aspect >= 1f)
        {
            boardWidth = fieldLongSide;
            boardDepth = fieldLongSide / aspect;
        }
        else
        {
            boardWidth = fieldLongSide * aspect;
            boardDepth = fieldLongSide;
        }
    }

    void BuildPalette(Texture2D source)
    {
        int count = Mathf.Clamp(paletteSize, 3, 8);
        Color[] centers = new Color[count];
        for (int i = 0; i < count; i++)
        {
            float u = (i + .5f) / count;
            float v = Mathf.Repeat(i * .6180339f + .23f, 1f);
            centers[i] = source.GetPixelBilinear(u, v);
        }
        const int sampleCount = 2048;
        for (int iteration = 0; iteration < 7; iteration++)
        {
            Vector3[] sums = new Vector3[count];
            int[] counts = new int[count];
            for (int i = 0; i < sampleCount; i++)
            {
                float u = Mathf.Repeat(i * .6180339f, 1f);
                float v = Mathf.Repeat(i * .4142135f + .17f, 1f);
                Color sample = source.GetPixelBilinear(u, v);
                int nearest = NearestPalette(sample, centers);
                sums[nearest] += new Vector3(sample.r, sample.g, sample.b);
                counts[nearest]++;
            }
            for (int i = 0; i < count; i++)
                if (counts[i] > 0) centers[i] = new Color(sums[i].x / counts[i], sums[i].y / counts[i], sums[i].z / counts[i]);
        }
        palette = new Color32[count];
        for (int i = 0; i < count; i++) palette[i] = centers[i];
    }

    void BuildHeightMap(Texture2D source)
    {
        terrainColumns = Mathf.Clamp(terrainColumns, 24, 128);
        terrainRows = Mathf.Clamp(terrainRows, 16, 96);
        terrainHeights = new float[terrainColumns * terrainRows];
        for (int z = 0; z < terrainRows; z++)
        for (int x = 0; x < terrainColumns; x++)
        {
            Color sample = source.GetPixelBilinear(1f - x / (float)(terrainColumns - 1), z / (float)(terrainRows - 1));
            terrainHeights[z * terrainColumns + x] = sample.r * .2126f + sample.g * .7152f + sample.b * .0722f;
        }
        for (int pass = 0; pass < heightSmoothingPasses; pass++)
        {
            float[] smoothed = new float[terrainHeights.Length];
            for (int z = 0; z < terrainRows; z++)
            for (int x = 0; x < terrainColumns; x++)
            {
                float sum = 0f; int samples = 0;
                for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                {
                    int sx = Mathf.Clamp(x + dx, 0, terrainColumns - 1);
                    int sz = Mathf.Clamp(z + dz, 0, terrainRows - 1);
                    sum += terrainHeights[sz * terrainColumns + sx]; samples++;
                }
                smoothed[z * terrainColumns + x] = sum / samples;
            }
            terrainHeights = smoothed;
        }
        // Stretch the image's actual luminance range to the full height range. Without
        // this, most photos only differ by a few tenths of a unit and look flat.
        float darkest = float.MaxValue, brightest = float.MinValue;
        for (int i = 0; i < terrainHeights.Length; i++)
        {
            darkest = Mathf.Min(darkest, terrainHeights[i]);
            brightest = Mathf.Max(brightest, terrainHeights[i]);
        }
        float range = Mathf.Max(.0001f, brightest - darkest);
        for (int i = 0; i < terrainHeights.Length; i++)
        {
            float normalized = Mathf.Clamp01((terrainHeights[i] - darkest) / range);
            terrainHeights[i] = Mathf.Pow(normalized, heightContrast) * terrainHeight;
        }
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
        player = new Vector3(-BoardWidth * .37f, 0f, -BoardDepth * .28f);
        rival = rivalDestination = new Vector3(BoardWidth * .37f, 0f, BoardDepth * .28f);
        CreateFloor();
        player.y = SampleTerrainHeight(player.x, player.z);
        rival.y = rivalDestination.y = SampleTerrainHeight(rival.x, rival.z);
        paintStampRoot = GetContainer("Paint Stamps");
        Transform playerPreview = playerRoot.Find("Slime Model Preview");
        if (playerPreview != null) playerPreview.gameObject.SetActive(false);
        playerObject = CreateActor("Body", new Color(.12f, .55f, 1f), player, playerRoot, playerModel);
        rivalObject = CreateActor("Body", new Color(1f, .2f, .3f), rival, rivalRoot, playerModel);
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
        terrainMesh = BuildTerrainMesh();
        var floor = new GameObject("Playable Image Terrain");
        terrainObject = floor;
        floor.name = "Playable Paint Map";
        floor.transform.SetParent(mapRoot);
        floor.AddComponent<MeshFilter>().sharedMesh = terrainMesh;
        floor.AddComponent<MeshRenderer>().material = NewMapMaterial();
        mapFloorCollider = floor.AddComponent<MeshCollider>();
        ((MeshCollider)mapFloorCollider).sharedMesh = terrainMesh;

        // This transparent overlay is the visible source of truth for paint. A later
        // colour write replaces the same texture cells, so paint can be overwritten.
        paintTexture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        var paintOverlay = new GameObject("Paint Overlay");
        paintOverlayObject = paintOverlay;
        paintOverlay.name = "Paint Overlay";
        paintOverlay.transform.SetParent(mapRoot);
        paintOverlay.AddComponent<MeshFilter>().sharedMesh = BuildOverlayMesh(terrainMesh, .025f);
        Material overlayMaterial = NewTransparentTextureMaterial(paintTexture);
        overlayMaterial.renderQueue = (int)RenderQueue.Transparent + 1;
        paintOverlay.AddComponent<MeshRenderer>().material = overlayMaterial;

        var border = GameObject.CreatePrimitive(PrimitiveType.Cube);
        border.name = "Map Base";
        border.transform.SetParent(mapRoot);
        border.transform.position = new Vector3(0f, -.12f, 0f);
        border.transform.localScale = new Vector3(BoardWidth + .3f, .2f, BoardDepth + .3f);
        border.GetComponent<Renderer>().material.color = new Color(.06f, .08f, .13f);
    }

    Mesh BuildTerrainMesh()
    {
        int vertexCount = terrainColumns * terrainRows;
        var vertices = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var triangles = new int[(terrainColumns - 1) * (terrainRows - 1) * 6];
        for (int z = 0; z < terrainRows; z++)
        for (int x = 0; x < terrainColumns; x++)
        {
            int index = z * terrainColumns + x;
            float x01 = x / (float)(terrainColumns - 1);
            float z01 = z / (float)(terrainRows - 1);
            vertices[index] = new Vector3((x01 - .5f) * BoardWidth, terrainHeights[index], (z01 - .5f) * BoardDepth);
            // Match the existing Plane's mirrored U direction so teams remain left/right.
            uvs[index] = new Vector2(1f - x01, z01);
        }
        int triangle = 0;
        for (int z = 0; z < terrainRows - 1; z++)
        for (int x = 0; x < terrainColumns - 1; x++)
        {
            int a = z * terrainColumns + x;
            int b = a + terrainColumns;
            triangles[triangle++] = a; triangles[triangle++] = b; triangles[triangle++] = a + 1;
            triangles[triangle++] = a + 1; triangles[triangle++] = b; triangles[triangle++] = b + 1;
        }
        var mesh = new Mesh { name = "Generated Image Terrain" };
        mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    Mesh BuildOverlayMesh(Mesh source, float normalOffset)
    {
        Vector3[] vertices = source.vertices;
        Vector3[] normals = source.normals;
        for (int i = 0; i < vertices.Length; i++) vertices[i] += normals[i] * normalOffset;
        var overlay = new Mesh { name = "Generated Paint Overlay" };
        overlay.indexFormat = IndexFormat.UInt32;
        overlay.vertices = vertices;
        overlay.uv = source.uv;
        overlay.triangles = source.triangles;
        overlay.normals = normals;
        overlay.RecalculateBounds();
        return overlay;
    }

    void RebuildTerrainGeometry()
    {
        terrainMesh = BuildTerrainMesh();
        terrainObject.GetComponent<MeshFilter>().sharedMesh = terrainMesh;
        MeshCollider collider = terrainObject.GetComponent<MeshCollider>();
        collider.sharedMesh = null;
        collider.sharedMesh = terrainMesh;
        if (paintOverlayObject != null)
            paintOverlayObject.GetComponent<MeshFilter>().sharedMesh = BuildOverlayMesh(terrainMesh, .025f);
        RefreshPaintTexture();
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

    GameObject CreateActor(string label, Color color, Vector3 position, Transform parent, GameObject modelPrefab = null)
    {
        var actor = new GameObject(label);
        actor.name = label;
        actor.transform.SetParent(parent);
        actor.transform.position = position + Vector3.up;
        actor.layer = LayerMask.NameToLayer("Ignore Raycast");
        if (modelPrefab != null)
        {
            Transform visualPivot = new GameObject("Visual Motion").transform;
            visualPivot.SetParent(actor.transform, false);
            GameObject model = Instantiate(modelPrefab, visualPivot);
            model.name = "Slime Model";
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(playerModelRotation);
            NormalizeModel(model.transform, 2f);
            if (parent == playerRoot)
            {
                playerVisualPivot = visualPivot;
                playerVisualBasePosition = visualPivot.localPosition;
            }
            else if (parent == rivalRoot)
            {
                rivalVisualPivot = visualPivot;
                rivalVisualBasePosition = visualPivot.localPosition;
            }
        }
        else
        {
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Capsule Visual";
            visual.transform.SetParent(actor.transform, false);
            Destroy(visual.GetComponent<CapsuleCollider>());
            visual.GetComponent<Renderer>().material.color = color;
        }
        return actor;
    }

    void AnimatePlayerVisual()
    {
        float horizontal = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float vertical = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        bool isMoving = horizontal * horizontal + vertical * vertical > .01f;
        AnimateSlimeVisual(playerVisualPivot, playerVisualBasePosition, isMoving, 0f);
        AnimateSlimeVisual(rivalVisualPivot, rivalVisualBasePosition, rivalIsMoving, 1.7f);
    }

    static void AnimateSlimeVisual(Transform visual, Vector3 basePosition, bool isMoving, float phaseOffset)
    {
        if (visual == null) return;
        float phase = Time.time * (isMoving ? 10f : 2.2f) + phaseOffset;
        float wave = Mathf.Sin(phase);
        float bounce = isMoving ? Mathf.Max(0f, wave) * .16f : wave * .025f;
        float squash = isMoving ? wave * .09f : wave * .018f;
        Vector3 targetScale = new Vector3(1f + squash, 1f - squash, 1f + squash);
        Vector3 targetPosition = basePosition + Vector3.up * bounce;

        visual.localScale = Vector3.Lerp(visual.localScale, targetScale, 14f * Time.deltaTime);
        visual.localPosition = Vector3.Lerp(visual.localPosition, targetPosition, 14f * Time.deltaTime);
    }

    static void NormalizeModel(Transform model, float targetHeight)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        if (bounds.size.y <= .0001f) return;
        model.localScale *= targetHeight / bounds.size.y;
        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        model.position += model.parent.position - bounds.center;
    }

    void CreateCameraRig()
    {
        cameraTarget = new GameObject("Camera Target").transform;
        cameraPivot = new GameObject("Camera Pivot").transform;
        cameraTarget.SetParent(playerRoot);
        cameraPivot.SetParent(cameraTarget);
        cameraTarget.position = playerObject.transform.position + Vector3.up * ThirdPersonTargetHeight;
        cameraPivot.localPosition = Vector3.zero;
        cameraTransform.SetParent(cameraPivot);
        desiredCameraDistance = Mathf.Clamp(ThirdPersonDistance, zoomInDistance, zoomOutDistance);
        cameraTransform.localPosition = new Vector3(0f, 0f, -ThirdPersonDistance);
        cameraTransform.localRotation = Quaternion.identity;
    }

    void Update()
    {
        if (!Application.isPlaying) return;
        InitializeRuntime();
        if (playerObject == null || rivalObject == null) return;
        if (finished) { if (Input.GetKeyDown(KeyCode.R) && (!multiplayerActive || multiplayerHost)) Restart(); return; }
        if (!multiplayerActive || multiplayerHost) remaining = Mathf.Max(0f, remaining - Time.deltaTime);
        if (!wallDown && remaining <= WallFallsAt) { wallDown = true; wallObject.SetActive(false); }
        MovePlayer();
        if (!multiplayerActive) MoveRival();
        AnimatePlayerVisual();
        // Painting is deliberate: movement alone never changes the map.
        if (Input.GetMouseButton(0) && TryGetMapHit(playerObject.transform, out RaycastHit playerHit))
        {
            if (AddPaintStamp(playerHit.point, selectedColor, ref hasPlayerStamp, ref lastPlayerStamp, "Player Paint"))
            {
                if (multiplayerActive) OnMultiplayerPaintRequested?.Invoke(playerHit.textureCoord, selectedColor, multiplayerTeam);
                else PaintAt(playerHit.textureCoord, selectedColor, 0);
            }
        }
        if (!multiplayerActive && TryGetMapHit(rivalObject.transform, out RaycastHit rivalHit))
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
        if (moveDirection.sqrMagnitude > .0001f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(moveDirection, Vector3.up);
            playerObject.transform.rotation = Quaternion.Slerp(
                playerObject.transform.rotation,
                targetRotation,
                playerTurnSpeed * Time.deltaTime);
        }
        if (playerController.isGrounded && playerVelocityY < 0f) playerVelocityY = -1.5f;
        if (Input.GetKeyDown(KeyCode.Space) && playerController.isGrounded) playerVelocityY = 6.1f;
        playerVelocityY += Physics.gravity.y * Time.deltaTime;
        playerController.Move((moveDirection * 9f + Vector3.up * playerVelocityY) * Time.deltaTime);
        Vector3 constrained = playerObject.transform.position;
        float leftLimit = -BoardWidth * .5f + .5f;
        float rightLimit = BoardWidth * .5f - .5f;
        if (!wallDown)
        {
            if (multiplayerActive && multiplayerTeam == 1) leftLimit = .45f;
            else rightLimit = -.45f;
        }
        constrained.x = Mathf.Clamp(constrained.x, leftLimit, rightLimit);
        constrained.z = Mathf.Clamp(constrained.z, -BoardDepth * .5f + .5f, BoardDepth * .5f - .5f);
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
        Vector3 previousPosition = rival;
        rival = Vector3.MoveTowards(rival, rivalDestination, 4.5f * Time.deltaTime);
        Vector3 moveDirection = rival - previousPosition;
        moveDirection.y = 0f;
        rivalIsMoving = moveDirection.sqrMagnitude > .000001f;
        if (rivalIsMoving)
        {
            Quaternion targetRotation = Quaternion.LookRotation(moveDirection, Vector3.up);
            rivalObject.transform.rotation = Quaternion.Slerp(
                rivalObject.transform.rotation,
                targetRotation,
                playerTurnSpeed * Time.deltaTime);
        }
        rival.y = SampleTerrainHeight(rival.x, rival.z);
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
        coverageDirty = true;
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
            faded.a = 175;
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

    int NearestPalette(Color color, Color[] colors)
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < colors.Length; i++)
        {
            float dr = colors[i].r - color.r;
            float dg = colors[i].g - color.g;
            float db = colors[i].b - color.b;
            float distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }

    float SampleTerrainHeight(float worldX, float worldZ)
    {
        if (terrainHeights == null) return 0f;
        float x = Mathf.Clamp01(worldX / BoardWidth + .5f) * (terrainColumns - 1);
        float z = Mathf.Clamp01(worldZ / BoardDepth + .5f) * (terrainRows - 1);
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        int x1 = Mathf.Min(x0 + 1, terrainColumns - 1), z1 = Mathf.Min(z0 + 1, terrainRows - 1);
        float tx = x - x0, tz = z - z0;
        float a = Mathf.Lerp(terrainHeights[z0 * terrainColumns + x0], terrainHeights[z0 * terrainColumns + x1], tx);
        float b = Mathf.Lerp(terrainHeights[z1 * terrainColumns + x0], terrainHeights[z1 * terrainColumns + x1], tx);
        return Mathf.Lerp(a, b, tz);
    }

    Vector3 ClampToBoard(Vector3 value)
    {
        value.x = Mathf.Clamp(value.x, -BoardWidth * .5f + .5f, BoardWidth * .5f - .5f);
        value.z = Mathf.Clamp(value.z, -BoardDepth * .5f + .5f, BoardDepth * .5f - .5f);
        return value;
    }

    float Score(int team)
    {
        GetCoverage(team, out int total, out _, out int correct, out _, out _);
        return 100f * correct / total;
    }

    void GetCoverage(int team, out int total, out int paintedCells, out int correctCells, out int wrongCells, out int foreignCells)
    {
        RefreshCoverageIfNeeded();
        total = coverageTotal[team];
        paintedCells = coveragePainted[team];
        correctCells = coverageCorrect[team];
        wrongCells = coverageWrong[team];
        foreignCells = coverageForeign[team];
    }

    // The target map is now high resolution, so score values are cached and recalculated
    // only after a paint change instead of once for every GUI label.
    void RefreshCoverageIfNeeded()
    {
        if (!coverageDirty) return;
        Array.Clear(coverageTotal, 0, coverageTotal.Length);
        Array.Clear(coveragePainted, 0, coveragePainted.Length);
        Array.Clear(coverageCorrect, 0, coverageCorrect.Length);
        Array.Clear(coverageWrong, 0, coverageWrong.Length);
        Array.Clear(coverageForeign, 0, coverageForeign.Length);
        for (int z = 0; z < Height; z++)
        for (int x = 0; x < Width; x++)
        {
            int team = TeamAt(x);
            coverageTotal[team]++;
            int index = Index(x, z);
            if (paint[index].a == 0) continue;
            coveragePainted[team]++;
            Color32 p = paint[index], target = targets[index];
            if (p.r == target.r && p.g == target.g && p.b == target.b) coverageCorrect[team]++;
            else coverageWrong[team]++;
            if (paintOwner[index] != team) coverageForeign[team]++;
        }
        coverageDirty = false;
    }

    void Restart()
    {
        remaining = TotalSeconds; wallDown = finished = false; selectedColor = 0;
        player = new Vector3(-BoardWidth * .37f, SampleTerrainHeight(-BoardWidth * .37f, -BoardDepth * .28f), -BoardDepth * .28f);
        rival = rivalDestination = new Vector3(BoardWidth * .37f, SampleTerrainHeight(BoardWidth * .37f, BoardDepth * .28f), BoardDepth * .28f);
        playerObject.transform.position = player + Vector3.up;
        rivalObject.transform.position = rival + Vector3.up;
        Array.Clear(paint, 0, paint.Length);
        for (int i = 0; i < paintOwner.Length; i++) paintOwner[i] = -1;
        coverageDirty = true;
        wallObject.SetActive(true); RefreshMapTexture(); RefreshPaintTexture();
        foreach (Transform stamp in paintStampRoot) Destroy(stamp.gameObject);
        hasPlayerStamp = hasRivalStamp = false;
        foreach (LineRenderer trail in playerTrails) if (trail != null) Destroy(trail.gameObject);
        playerTrails.Clear(); activeTrail = activeRivalTrail = null;
        activeTrailColor = activeRivalTrailColor = -1;
    }

    public event Action<Vector2, int, int> OnMultiplayerPaintRequested;
    public bool IsBattleReady => runtimeInitialized && playerObject != null && rivalObject != null;
    public Vector3 LocalPlayerPosition => playerObject != null ? playerObject.transform.position : Vector3.zero;
    public float RemainingSeconds => remaining;
    public bool IsWallDown => wallDown;
    public bool IsFinished => finished;

    public void EnableMultiplayer(int team, bool isHost)
    {
        multiplayerActive = true;
        multiplayerHost = isHost;
        multiplayerTeam = team;
        if (!IsBattleReady) return;
        Vector3 localSpawn = team == 0 ? new Vector3(-BoardWidth * .37f, 0f, -BoardDepth * .28f) : new Vector3(BoardWidth * .37f, 0f, BoardDepth * .28f);
        Vector3 remoteSpawn = team == 0 ? new Vector3(BoardWidth * .37f, 0f, BoardDepth * .28f) : new Vector3(-BoardWidth * .37f, 0f, -BoardDepth * .28f);
        localSpawn.y = SampleTerrainHeight(localSpawn.x, localSpawn.z);
        remoteSpawn.y = SampleTerrainHeight(remoteSpawn.x, remoteSpawn.z);
        player = localSpawn;
        rival = remoteSpawn;
        playerObject.transform.position = localSpawn + Vector3.up;
        rivalObject.transform.position = remoteSpawn + Vector3.up;
        playerObject.GetComponent<Renderer>().material.color = team == 0 ? new Color(.12f, .55f, 1f) : new Color(1f, .2f, .3f);
        rivalObject.GetComponent<Renderer>().material.color = team == 0 ? new Color(1f, .2f, .3f) : new Color(.12f, .55f, 1f);
    }

    public void ApplyRemotePlayerState(Vector3 position)
    {
        if (rivalObject == null) return;
        rival = ClampToBoard(position - Vector3.up);
        rival.y = SampleTerrainHeight(rival.x, rival.z);
        rivalObject.transform.position = rival + Vector3.up;
    }

    public void ApplyNetworkPaint(Vector2 mapUv, int color, int owner)
    {
        if (color >= 0 && color < palette.Length) PaintAt(mapUv, color, owner);
    }

    public void ApplyNetworkClock(float seconds, bool barrierDown, bool matchFinished)
    {
        if (multiplayerHost) return;
        remaining = Mathf.Clamp(seconds, 0f, TotalSeconds);
        wallDown = barrierDown;
        finished = matchFinished;
        if (wallObject != null) wallObject.SetActive(!wallDown);
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
        return index >= 0 && index < palette.Length ? $"색상 {index + 1}" : "없음";
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
        // Positive wheel movement brings the camera closer; negative movement pulls it back.
        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) > .0001f)
            desiredCameraDistance = Mathf.Clamp(desiredCameraDistance - scroll * zoomSensitivity, zoomInDistance, zoomOutDistance);
        if (Input.GetMouseButton(1))
        {
            cameraYaw += Input.GetAxis("Mouse X") * horizontalLookSensitivity;
            cameraPitch = Mathf.Clamp(cameraPitch - Input.GetAxis("Mouse Y") * verticalLookSensitivity, 5f, 42f);
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        cameraTarget.position = Vector3.Lerp(cameraTarget.position, playerObject.transform.position + Vector3.up * ThirdPersonTargetHeight, 12f * Time.deltaTime);
        cameraPivot.rotation = Quaternion.Euler(cameraPitch, cameraYaw, 0f);
        float distance = desiredCameraDistance;
        if (Physics.SphereCast(cameraPivot.position, .25f, -cameraPivot.forward, out RaycastHit hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            distance = Mathf.Max(2.5f, hit.distance - .2f);
        cameraTransform.localPosition = Vector3.Lerp(cameraTransform.localPosition, new Vector3(0f, 0f, -distance), 16f * Time.deltaTime);
        cameraTransform.localRotation = Quaternion.identity;
    }
}
