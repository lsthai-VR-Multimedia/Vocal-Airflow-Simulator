using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

public class Voxelizer : MonoBehaviour
{
    [SerializeField] private int resolutionX = 64;
    [SerializeField] private int resolutionY = 128;
    [SerializeField] private int resolutionZ = 64;

    [Header("Chia nhỏ công việc khi Revoxelize (batch cố định)")]
    [Tooltip("Ngân sách thời gian mỗi khối xử lý trước khi nhường 1 khung hình (ms) — giống hệt cơ chế buildBudgetMs của thuật toán Confluence, dùng Stopwatch thời gian thực thay vì đếm số phần tử.")]
    [SerializeField] private float buildBudgetMs = 40f;
    private System.Diagnostics.Stopwatch _bsw;

    bool BudgetElapsed()
    {
        if (_bsw.ElapsedMilliseconds >= buildBudgetMs)
        {
            _bsw.Restart();
            return true;
        }
        return false;
    }

    // Gói dữ liệu một "thế hệ" kết quả voxel hóa — mỗi lượt Revoxelize tạo
    // một gói mới hoàn toàn độc lập, chỉ công bố (gán vào _current) khi xong.
    private class VoxelSet
    {
        public bool[,,] grid;
        public Bounds bounds;
        public Vector3 voxelSize;
        public float threshold;
    }

    private VoxelSet _current;
    private Mesh _mesh;
    private Transform _transform;
    private bool _done = false;
    private GameObject _voxelMeshGO;
    private MeshRenderer _sourceMeshRenderer;

    public bool[,,] VoxelGrid => _current?.grid;
    public Bounds Bounds => _current != null ? _current.bounds : default;
    public Vector3 VoxelSize => _current != null ? _current.voxelSize : Vector3.one;
    public bool IsDone => _done;

    void Start()
    {
        WarmUpJit();

        _mesh = GetComponent<MeshFilter>().mesh;
        _transform = transform;
        _sourceMeshRenderer = GetComponent<MeshRenderer>();

        var vs = new VoxelSet();
        vs.bounds = _sourceMeshRenderer.bounds;
        vs.voxelSize = new Vector3(
            vs.bounds.size.x / resolutionX,
            vs.bounds.size.y / resolutionY,
            vs.bounds.size.z / resolutionZ
        );
        vs.threshold = Mathf.Min(vs.voxelSize.x, vs.voxelSize.z) * 0.5f;
        vs.grid = new bool[resolutionX, resolutionY, resolutionZ];

        VoxelizeInto(vs);
        FloodFillOutsideInto(vs);
        vs.grid = CloseVoxels(vs.grid);
        vs.grid = SmoothCorners(vs.grid);
        vs.grid = RemoveSmallClusters(vs.grid);

        _current = vs;   // công bố kết quả — 1 lần gán duy nhất
        _done = true;
        Debug.Log("Voxelize done. Nhấn E để hiện debug, Q để ẩn.");
    }

    // Chạy các hàm hot-path (TryIntersect, ChainSegments, IsPointInContour) trên dữ liệu GIẢ,
    // cực nhỏ, TRƯỚC khi voxelize thật bắt đầu — để .NET/Mono JIT kịp biên dịch lại bản tối ưu
    // (tiered compilation) NGAY TỪ ĐẦU. Cùng lý do với warm-up bên VectorFieldBaker.MeasureCrossSection.
    void WarmUpJit()
    {
        // 1 tứ diện đơn giản làm mesh giả
        Vector3[] verts = {
            new Vector3(0, 0, 0), new Vector3(1, 0, 0),
            new Vector3(0.5f, 0, 1), new Vector3(0.5f, 1, 0.5f)
        };
        int[] tris = { 0, 1, 2, 0, 1, 3, 1, 2, 3, 2, 0, 3 };

        for (int i = 0; i < 100; i++)
        {
            float y = 0.1f + (i % 8) * 0.1f; // vài mức cắt khác nhau
            List<Vector2[]> segments = new List<Vector2[]>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 v0 = verts[tris[t]], v1 = verts[tris[t + 1]], v2 = verts[tris[t + 2]];
                List<Vector2> pts = new List<Vector2>();
                TryIntersect(v0, v1, y, pts);
                TryIntersect(v1, v2, y, pts);
                TryIntersect(v2, v0, y, pts);
                if (pts.Count == 2) segments.Add(new Vector2[] { pts[0], pts[1] });
            }
            var contours = ChainSegments(segments, 0.001f);
            foreach (var c in contours)
                IsPointInContour(new Vector2(0.4f, 0.4f), c);
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.E)) ShowVoxelMesh();
        if (Input.GetKeyDown(KeyCode.Q)) HideVoxelMesh();

        if (Input.GetKeyDown(KeyCode.R))
        {
            VectorFieldBaker baker = GetComponent<VectorFieldBaker>();
            if (baker != null && baker.IsBaked) baker.ShowVectorField();
        }
        if (Input.GetKeyDown(KeyCode.T))
        {
            VectorFieldBaker baker = GetComponent<VectorFieldBaker>();
            if (baker != null) baker.HideVectorField();
        }
    }

    // ==================== BUCKET TAM GIÁC THEO LỚP Y ====================

    List<int>[] BuildYBuckets(Vector3[] worldVerts, int[] triangles, Bounds bounds, Vector3 voxelSize)
    {
        List<int>[] buckets = new List<int>[resolutionY];
        for (int i = 0; i < resolutionY; i++) buckets[i] = new List<int>();

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 v0 = worldVerts[triangles[i]];
            Vector3 v1 = worldVerts[triangles[i + 1]];
            Vector3 v2 = worldVerts[triangles[i + 2]];

            float minY = Mathf.Min(v0.y, v1.y, v2.y);
            float maxY = Mathf.Max(v0.y, v1.y, v2.y);

            int loLayer = Mathf.Clamp(Mathf.FloorToInt((minY - bounds.min.y) / voxelSize.y), 0, resolutionY - 1);
            int hiLayer = Mathf.Clamp(Mathf.CeilToInt((maxY - bounds.min.y) / voxelSize.y), 0, resolutionY - 1);

            for (int layer = loLayer; layer <= hiLayer; layer++)
                buckets[layer].Add(i);
        }

        return buckets;
    }

    List<List<Vector2>> GetContoursAtYBucketed(Vector3[] worldVerts, int[] triangles, float y, List<int> bucket, float threshold)
    {
        List<Vector2[]> segments = new List<Vector2[]>();

        foreach (int i in bucket)
        {
            Vector3 v0 = worldVerts[triangles[i]];
            Vector3 v1 = worldVerts[triangles[i + 1]];
            Vector3 v2 = worldVerts[triangles[i + 2]];

            List<Vector2> pts = new List<Vector2>();
            TryIntersect(v0, v1, y, pts);
            TryIntersect(v1, v2, y, pts);
            TryIntersect(v2, v0, y, pts);

            if (pts.Count == 2)
                segments.Add(new Vector2[] { pts[0], pts[1] });
        }

        return ChainSegments(segments, threshold);
    }

    // ==================== REVOXELIZE (nhiều lượt độc lập, gối nhau được) ====================
    // Mỗi lượt gọi hàm này tạo một VoxelSet CỤC BỘ hoàn toàn riêng (không đụng
    // instance field nào trong lúc tính). Khi xong, gọi onComplete để trả kết
    // quả cho caller (VectorFieldBaker giữ bản riêng của lượt đó), ĐỒNG THỜI
    // công bố vào _current bằng một lần gán duy nhất (không yield giữa chừng)
    // — lượt nào hoàn tất sau cùng sẽ là kết quả hiển thị/dùng cho debug.
    public IEnumerator RevoxelizeCoroutine(System.Action<bool[,,], Bounds, Vector3> onComplete)
    {
        _bsw = System.Diagnostics.Stopwatch.StartNew();
        if (_mesh == null) _mesh = GetComponent<MeshFilter>().mesh;
        _mesh.RecalculateBounds();

        var vs = new VoxelSet();
        vs.bounds = _sourceMeshRenderer.bounds;
        vs.voxelSize = new Vector3(
            vs.bounds.size.x / resolutionX,
            vs.bounds.size.y / resolutionY,
            vs.bounds.size.z / resolutionZ
        );
        vs.threshold = Mathf.Min(vs.voxelSize.x, vs.voxelSize.z) * 0.5f;
        vs.grid = new bool[resolutionX, resolutionY, resolutionZ];

        // Snapshot vertex/triangle NGAY LÚC NÀY — Mesh.vertices trả về bản sao,
        // nên an toàn dù mesh tiếp tục biến dạng ở các frame sau trong khi
        // lượt này vẫn đang tính.
        Vector3[] vertices = _mesh.vertices;
        int[] triangles = _mesh.triangles;
        Vector3[] worldVerts = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            worldVerts[i] = _transform.TransformPoint(vertices[i]);

        List<int>[] buckets = BuildYBuckets(worldVerts, triangles, vs.bounds, vs.voxelSize);
        yield return null;

        // Chạy đơn luồng, thẳng trên main thread — không dùng Task.Run/Parallel.For nữa (tránh
        // "giấu" tải khỏi FPS). Nhường 1 khung hình SAU MỖI LÁT Y — đơn vị tự nhiên, giống cách
        // Confluence nghỉ giữa các bước lô-gic. Một lát Y tự nó vẫn tỉ lệ với độ phân giải (lưới
        // to hơn, mỗi lát xử lý nhiều ô X×Z hơn) nên FPS vẫn phản ánh đúng khác biệt, mà không
        // đứng hình hoàn toàn như khi gộp hết resolutionY lát vào 1 khung hình duy nhất.
        for (int yi = 0; yi < resolutionY; yi++)
        {
            float yWorld = vs.bounds.min.y + (yi + 0.5f) * vs.voxelSize.y;
            List<List<Vector2>> contours = GetContoursAtYBucketed(worldVerts, triangles, yWorld, buckets[yi], vs.threshold);

            for (int xi = 0; xi < resolutionX; xi++)
                for (int zi = 0; zi < resolutionZ; zi++)
                {
                    float xWorld = vs.bounds.min.x + (xi + 0.5f) * vs.voxelSize.x;
                    float zWorld = vs.bounds.min.z + (zi + 0.5f) * vs.voxelSize.z;
                    Vector2 point = new Vector2(xWorld, zWorld);

                    bool inside = false;
                    foreach (var contour in contours)
                        if (IsPointInContour(point, contour))
                            inside = !inside;

                    vs.grid[xi, yi, zi] = inside;
                }

            // Kiểm tra ngân sách SAU MỖI LÁT Y (đơn vị tự nhiên, không cắt ngang giữa 1 lát) —
            // nếu vài lát liên tiếp đều nhẹ, gộp lại xử lý chung 1 khung hình cho tới khi đủ
            // ~buildBudgetMs; nếu 1 lát đã nặng vượt ngân sách, tách riêng ra ngay. Cách này cho
            // FPS ổn định đều quanh mức mục tiêu (40ms/khung ≈ 25fps) thay vì giật lên xuống
            // thất thường khi nhường quá thường xuyên (mỗi lát 1 khung hình bất kể nặng nhẹ).
            if (BudgetElapsed())
                yield return null;
        }

        yield return FloodFillOutsideCoroutine(vs);
        yield return null;

        vs.grid = CloseVoxels(vs.grid);
        yield return null;

        vs.grid = SmoothCorners(vs.grid);
        yield return null;

        vs.grid = RemoveSmallClusters(vs.grid);
        yield return null;

        // Công bố kết quả — một lần gán duy nhất, không yield xen giữa
        _current = vs;

        onComplete?.Invoke(vs.grid, vs.bounds, vs.voxelSize);
    }

    IEnumerator FloodFillOutsideCoroutine(VoxelSet vs)
    {
        bool[,,] outside = new bool[resolutionX, resolutionY, resolutionZ];
        Queue<Vector3Int> queue = new Queue<Vector3Int>();

        for (int y = 0; y < resolutionY; y++)
            for (int z = 0; z < resolutionZ; z++)
            {
                TryEnqueue(0, y, z, vs.grid, outside, queue);
                TryEnqueue(resolutionX - 1, y, z, vs.grid, outside, queue);
            }
        for (int x = 0; x < resolutionX; x++)
            for (int z = 0; z < resolutionZ; z++)
            {
                TryEnqueue(x, 0, z, vs.grid, outside, queue);
                TryEnqueue(x, resolutionY - 1, z, vs.grid, outside, queue);
            }
        for (int x = 0; x < resolutionX; x++)
            for (int y = 0; y < resolutionY; y++)
            {
                TryEnqueue(x, y, 0, vs.grid, outside, queue);
                TryEnqueue(x, y, resolutionZ - 1, vs.grid, outside, queue);
            }

        yield return null;

        int[] dx = { 1, -1, 0, 0, 0, 0 };
        int[] dy = { 0, 0, 1, -1, 0, 0 };
        int[] dz = { 0, 0, 0, 0, 1, -1 };

        int counter = 0;
        while (queue.Count > 0)
        {
            Vector3Int cur = queue.Dequeue();
            for (int d = 0; d < 6; d++)
                TryEnqueue(cur.x + dx[d], cur.y + dy[d], cur.z + dz[d], vs.grid, outside, queue);

            counter++;
            if (counter >= 1000)
            {
                counter = 0;
                if (BudgetElapsed())
                    yield return null;
            }
        }

        for (int x = 0; x < resolutionX; x++)
            for (int y = 0; y < resolutionY; y++)
                for (int z = 0; z < resolutionZ; z++)
                    vs.grid[x, y, z] = !outside[x, y, z];
    }

    void TryEnqueue(int x, int y, int z, bool[,,] grid, bool[,,] outside, Queue<Vector3Int> queue)
    {
        if (x < 0 || x >= resolutionX) return;
        if (y < 0 || y >= resolutionY) return;
        if (z < 0 || z >= resolutionZ) return;
        if (outside[x, y, z]) return;
        if (grid[x, y, z]) return;
        outside[x, y, z] = true;
        queue.Enqueue(new Vector3Int(x, y, z));
    }

    bool[,,] CloseVoxels(bool[,,] grid)
    {
        // Dilate/Erode ở đây dùng đếm số lân cận (không phải phép đóng hình thái học "thật"
        // đối xứng qua 1 ngưỡng duy nhất), nên lặp Dilate(3)+Erode(3) hai lượt với CÙNG
        // ngưỡng lỏng (3/26) sẽ khiến khối "bên trong" phình ra ngoài biên thật một lớp
        // mỏng, đều khắp mọi nơi — không riêng chỗ thắt (đây là nguyên nhân hạt tràn ra
        // ngoài rải rác khắp nơi sau khi tăng lên 2 lượt closing).
        // Dùng Erode với ngưỡng CAO hơn Dilate để kéo bề mặt lại gần biên thật: vùng thắt cổ
        // chai sau khi được Dilate lấp đầy sẽ có RẤT NHIỀU lân cận true (lòng ống kín, gần
        // 26 lân cận), nên không bị ngưỡng erode cao này xóa mất; chỉ lớp "phình dư" mỏng ở
        // ngoài biên thật (ít lân cận true hơn) mới bị loại bỏ.
        grid = Dilate(grid, 3);
        grid = Dilate(grid, 3);
        grid = Erode(grid, 6);
        grid = Erode(grid, 6);
        return grid;
    }

    [Range(1, 4)]
    [Tooltip("Số lượt 'mài góc' — chỉ Erode, KHÔNG Dilate bù lại sau CloseVoxels. Một viên " +
             "voxel nằm đúng góc lồi ra ngoài đường cong thật của mesh thường có ÍT lân cận " +
             "'trong' hơn (phần lớn xung quanh nó là 'ngoài'), nên bị loại bỏ trước — kéo biên " +
             "lưới voxel lại gần đường cong thật hơn, giảm sai số hệ thống ở các nhánh nhỏ (nơi " +
             "chỉ 1-3 voxel ngang qua đường kính ống, góc lồi chiếm tỉ lệ lớn). Tăng số này nếu " +
             "hạt vẫn còn tràn ra nhiều ở nhánh nhỏ, đổi lại ống có thể hẹp lại một chút so với " +
             "thật (đặc biệt ở nhánh vốn đã nhỏ).")]
    [SerializeField] private int smoothCornerPasses = 1;

    // "Điêu khắc" bớt các góc voxel lồi ra ngoài đường cong thật — CHỈ Erode, không Dilate bù
    // lại (khác CloseVoxels, vốn Dilate rồi Erode để LẤP LỖ chứ không phải để mài biên). Vì
    // lưới voxel về bản chất là các "viên gạch" xếp chồng mô phỏng 1 mặt cong, tại mỗi lát cắt
    // ngang đường viền bậc thang do voxel tạo ra có thể lệch ra CẢ 2 PHÍA so với đường cong thật
    // (có chỗ góc gạch lồi ra ngoài, có chỗ lõm vào trong) — không phải lúc nào cũng lệch cùng
    // 1 hướng. Threshold=13 (~một nửa 26 lân cận) chỉ loại các voxel có ít hơn một nửa số lân
    // cận là "trong" — đúng đặc điểm của voxel nằm ở góc lồi (phần lớn xung quanh là "ngoài"),
    // không đụng tới voxel nằm sâu trong lòng ống (gần như toàn bộ 26 lân cận đều "trong").
    bool[,,] SmoothCorners(bool[,,] grid)
    {
        for (int i = 0; i < smoothCornerPasses; i++)
            grid = Erode(grid, 13);
        return grid;
    }

    bool[,,] Dilate(bool[,,] grid, int threshold)
    {
        bool[,,] result = (bool[,,])grid.Clone();
        for (int x = 0; x < resolutionX; x++)
            for (int y = 0; y < resolutionY; y++)
                for (int z = 0; z < resolutionZ; z++)
                {
                    if (grid[x, y, z]) continue;
                    if (CountNeighbors(grid, x, y, z) >= threshold)
                        result[x, y, z] = true;
                }
        return result;
    }

    bool[,,] Erode(bool[,,] grid, int threshold)
    {
        bool[,,] result = (bool[,,])grid.Clone();
        for (int x = 0; x < resolutionX; x++)
            for (int y = 0; y < resolutionY; y++)
                for (int z = 0; z < resolutionZ; z++)
                {
                    if (!grid[x, y, z]) continue;
                    if (CountNeighbors(grid, x, y, z) < threshold)
                        result[x, y, z] = false;
                }
        return result;
    }

    int CountNeighbors(bool[,,] grid, int x, int y, int z)
    {
        int count = 0;
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    int nx = x + dx, ny = y + dy, nz = z + dz;
                    if (nx < 0 || nx >= resolutionX) continue;
                    if (ny < 0 || ny >= resolutionY) continue;
                    if (nz < 0 || nz >= resolutionZ) continue;
                    if (grid[nx, ny, nz]) count++;
                }
        return count;
    }

    bool[,,] RemoveSmallClusters(bool[,,] grid)
    {
        bool[,,] visited = new bool[resolutionX, resolutionY, resolutionZ];
        List<List<Vector3Int>> clusters = new List<List<Vector3Int>>();

        int[] dx = { 1, -1, 0, 0, 0, 0 };
        int[] dy = { 0, 0, 1, -1, 0, 0 };
        int[] dz = { 0, 0, 0, 0, 1, -1 };

        for (int x = 0; x < resolutionX; x++)
            for (int y = 0; y < resolutionY; y++)
                for (int z = 0; z < resolutionZ; z++)
                {
                    if (!grid[x, y, z] || visited[x, y, z]) continue;

                    List<Vector3Int> cluster = new List<Vector3Int>();
                    Queue<Vector3Int> queue = new Queue<Vector3Int>();
                    queue.Enqueue(new Vector3Int(x, y, z));
                    visited[x, y, z] = true;

                    while (queue.Count > 0)
                    {
                        Vector3Int c = queue.Dequeue();
                        cluster.Add(c);

                        for (int d = 0; d < 6; d++)
                        {
                            int nx = c.x + dx[d], ny = c.y + dy[d], nz = c.z + dz[d];
                            if (nx < 0 || nx >= resolutionX) continue;
                            if (ny < 0 || ny >= resolutionY) continue;
                            if (nz < 0 || nz >= resolutionZ) continue;
                            if (!grid[nx, ny, nz] || visited[nx, ny, nz]) continue;

                            visited[nx, ny, nz] = true;
                            queue.Enqueue(new Vector3Int(nx, ny, nz));
                        }
                    }
                    clusters.Add(cluster);
                }

        int maxSize = 0;
        foreach (var c in clusters)
            if (c.Count > maxSize) maxSize = c.Count;

        int minSize = Mathf.Max(5, maxSize / 20);

        bool[,,] result = (bool[,,])grid.Clone();
        foreach (var cluster in clusters)
            if (cluster.Count < minSize)
                foreach (var v in cluster)
                    result[v.x, v.y, v.z] = false;

        return result;
    }

    void ShowVoxelMesh()
    {
        if (_current == null) return;
        _sourceMeshRenderer.enabled = false;

        if (_voxelMeshGO != null)
        {
            _voxelMeshGO.SetActive(true);
            return;
        }

        var vs = _current;
        List<Vector3> insideVerts = new List<Vector3>();
        List<int> insideTris = new List<int>();
        List<Vector3> outsideVerts = new List<Vector3>();
        List<int> outsideTris = new List<int>();
        Vector3 half = vs.voxelSize * 0.45f;

        for (int xi = 0; xi < resolutionX; xi++)
            for (int yi = 0; yi < resolutionY; yi++)
                for (int zi = 0; zi < resolutionZ; zi++)
                {
                    Vector3 c = new Vector3(
                        vs.bounds.min.x + (xi + 0.5f) * vs.voxelSize.x,
                        vs.bounds.min.y + (yi + 0.5f) * vs.voxelSize.y,
                        vs.bounds.min.z + (zi + 0.5f) * vs.voxelSize.z
                    );

                    if (vs.grid[xi, yi, zi])
                        AddCube(c, half, insideVerts, insideTris);
                    else
                        AddCube(c, half, outsideVerts, outsideTris);
                }

        _voxelMeshGO = new GameObject("VoxelDebugMesh");
        CreateSubMesh(_voxelMeshGO, "Inside", insideVerts, insideTris, new Color(0f, 1f, 1f, 0.8f));
        CreateSubMesh(_voxelMeshGO, "Outside", outsideVerts, outsideTris, new Color(1f, 0f, 0f, 0.01f));

        Debug.Log($"Inside: {insideVerts.Count / 8} | Outside: {outsideVerts.Count / 8} voxels");
    }

    void HideVoxelMesh()
    {
        _sourceMeshRenderer.enabled = true;
        if (_voxelMeshGO != null)
            _voxelMeshGO.SetActive(false);
    }

    void CreateSubMesh(GameObject parent, string name,
                       List<Vector3> verts, List<int> tris, Color color)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent.transform);

        MeshFilter mf = go.AddComponent<MeshFilter>();
        MeshRenderer mr = go.AddComponent<MeshRenderer>();

        Material mat = new Material(Shader.Find("Standard"));
        mat.color = color;
        mat.SetFloat("_Mode", 3);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.renderQueue = 3000;
        mr.material = mat;

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = verts.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mf.mesh = mesh;
    }

    void AddCube(Vector3 c, Vector3 h, List<Vector3> verts, List<int> tris)
    {
        int b = verts.Count;
        verts.Add(c + new Vector3(-h.x, -h.y, -h.z));
        verts.Add(c + new Vector3(h.x, -h.y, -h.z));
        verts.Add(c + new Vector3(h.x, h.y, -h.z));
        verts.Add(c + new Vector3(-h.x, h.y, -h.z));
        verts.Add(c + new Vector3(-h.x, -h.y, h.z));
        verts.Add(c + new Vector3(h.x, -h.y, h.z));
        verts.Add(c + new Vector3(h.x, h.y, h.z));
        verts.Add(c + new Vector3(-h.x, h.y, h.z));

        int[] faces = {
            0,2,1, 0,3,2,
            4,5,6, 4,6,7,
            0,1,5, 0,5,4,
            2,3,7, 2,7,6,
            0,4,7, 0,7,3,
            1,2,6, 1,6,5
        };
        foreach (int f in faces) tris.Add(b + f);
    }

    // ==================== BẢN ĐỒNG BỘ (chỉ dùng cho Start) ====================

    void VoxelizeInto(VoxelSet vs)
    {
        Vector3[] vertices = _mesh.vertices;
        int[] triangles = _mesh.triangles;

        Vector3[] worldVerts = new Vector3[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            worldVerts[i] = _transform.TransformPoint(vertices[i]);

        for (int yi = 0; yi < resolutionY; yi++)
        {
            float yWorld = vs.bounds.min.y + (yi + 0.5f) * vs.voxelSize.y;
            List<List<Vector2>> contours = GetContoursAtY(worldVerts, triangles, yWorld, vs.threshold);

            for (int xi = 0; xi < resolutionX; xi++)
                for (int zi = 0; zi < resolutionZ; zi++)
                {
                    float xWorld = vs.bounds.min.x + (xi + 0.5f) * vs.voxelSize.x;
                    float zWorld = vs.bounds.min.z + (zi + 0.5f) * vs.voxelSize.z;
                    Vector2 point = new Vector2(xWorld, zWorld);

                    bool inside = false;
                    foreach (var contour in contours)
                        if (IsPointInContour(point, contour))
                            inside = !inside;

                    vs.grid[xi, yi, zi] = inside;
                }
        }
    }

    void FloodFillOutsideInto(VoxelSet vs)
    {
        bool[,,] outside = new bool[resolutionX, resolutionY, resolutionZ];
        Queue<Vector3Int> queue = new Queue<Vector3Int>();

        for (int y = 0; y < resolutionY; y++)
            for (int z = 0; z < resolutionZ; z++)
            {
                TryEnqueue(0, y, z, vs.grid, outside, queue);
                TryEnqueue(resolutionX - 1, y, z, vs.grid, outside, queue);
            }
        for (int x = 0; x < resolutionX; x++)
            for (int z = 0; z < resolutionZ; z++)
            {
                TryEnqueue(x, 0, z, vs.grid, outside, queue);
                TryEnqueue(x, resolutionY - 1, z, vs.grid, outside, queue);
            }
        for (int x = 0; x < resolutionX; x++)
            for (int y = 0; y < resolutionY; y++)
            {
                TryEnqueue(x, y, 0, vs.grid, outside, queue);
                TryEnqueue(x, y, resolutionZ - 1, vs.grid, outside, queue);
            }

        int[] dx = { 1, -1, 0, 0, 0, 0 };
        int[] dy = { 0, 0, 1, -1, 0, 0 };
        int[] dz = { 0, 0, 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            Vector3Int cur = queue.Dequeue();
            for (int d = 0; d < 6; d++)
                TryEnqueue(cur.x + dx[d], cur.y + dy[d], cur.z + dz[d], vs.grid, outside, queue);
        }

        for (int x = 0; x < resolutionX; x++)
            for (int y = 0; y < resolutionY; y++)
                for (int z = 0; z < resolutionZ; z++)
                    vs.grid[x, y, z] = !outside[x, y, z];
    }

    List<List<Vector2>> GetContoursAtY(Vector3[] worldVerts, int[] triangles, float y, float threshold)
    {
        List<Vector2[]> segments = new List<Vector2[]>();

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 v0 = worldVerts[triangles[i]];
            Vector3 v1 = worldVerts[triangles[i + 1]];
            Vector3 v2 = worldVerts[triangles[i + 2]];

            List<Vector2> pts = new List<Vector2>();
            TryIntersect(v0, v1, y, pts);
            TryIntersect(v1, v2, y, pts);
            TryIntersect(v2, v0, y, pts);

            if (pts.Count == 2)
                segments.Add(new Vector2[] { pts[0], pts[1] });
        }

        return ChainSegments(segments, threshold);
    }

    void TryIntersect(Vector3 a, Vector3 b, float y, List<Vector2> result)
    {
        if ((a.y < y && b.y >= y) || (b.y < y && a.y >= y))
        {
            float t = (y - a.y) / (b.y - a.y);
            Vector3 p = Vector3.Lerp(a, b, t);
            result.Add(new Vector2(p.x, p.z));
        }
    }

    List<List<Vector2>> ChainSegments(List<Vector2[]> segments, float threshold)
    {
        List<List<Vector2>> contours = new List<List<Vector2>>();
        bool[] used = new bool[segments.Count];

        for (int i = 0; i < segments.Count; i++)
        {
            if (used[i]) continue;

            List<Vector2> contour = new List<Vector2>();
            contour.Add(segments[i][0]);
            contour.Add(segments[i][1]);
            used[i] = true;

            bool found = true;
            while (found)
            {
                found = false;
                Vector2 last = contour[contour.Count - 1];

                if (contour.Count > 2 &&
                    Vector2.Distance(last, contour[0]) < threshold)
                    break;

                for (int j = 0; j < segments.Count; j++)
                {
                    if (used[j]) continue;

                    Vector2 a = segments[j][0];
                    Vector2 b = segments[j][1];

                    if (Vector2.Distance(last, a) < threshold)
                    {
                        contour.Add(b); used[j] = true; found = true; break;
                    }
                    else if (Vector2.Distance(last, b) < threshold)
                    {
                        contour.Add(a); used[j] = true; found = true; break;
                    }
                }
            }

            if (contour.Count >= 3)
                contours.Add(contour);
        }

        return contours;
    }

    bool IsPointInContour(Vector2 point, List<Vector2> contour)
    {
        int n = contour.Count;
        bool inside = false;

        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 ci = contour[i];
            Vector2 cj = contour[j];

            if (((ci.y > point.y) != (cj.y > point.y)) &&
                (point.x < (cj.x - ci.x) * (point.y - ci.y) / (cj.y - ci.y) + ci.x))
                inside = !inside;
        }

        return inside;
    }

    public bool IsInside(Vector3 worldPos)
    {
        if (_current == null) return false;
        var vs = _current;

        int xi = Mathf.FloorToInt((worldPos.x - vs.bounds.min.x) / vs.voxelSize.x);
        int yi = Mathf.FloorToInt((worldPos.y - vs.bounds.min.y) / vs.voxelSize.y);
        int zi = Mathf.FloorToInt((worldPos.z - vs.bounds.min.z) / vs.voxelSize.z);

        if (xi < 0 || xi >= resolutionX) return false;
        if (yi < 0 || yi >= resolutionY) return false;
        if (zi < 0 || zi >= resolutionZ) return false;

        return vs.grid[xi, yi, zi];
    }
}