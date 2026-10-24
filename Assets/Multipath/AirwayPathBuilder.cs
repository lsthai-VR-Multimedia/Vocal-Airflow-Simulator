using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Xây dựng multi-path branching airway network từ mesh phổi.
/// Thuật toán: Level-set skeletonization (Reeb graph).
///   1. Dijkstra từ cuống phổi -> geodesic distance mọi đỉnh
///   2. Chia thành các "band" (vành đai) theo distance
///   3. Mỗi band tìm connected components. Ống chia đôi => 1 component -> 2 => điểm rẽ nhánh
///   4. Nối các component giữa band liền kề => cây phân nhánh thực sự
///
/// Điểm VÀNG = đầu đường thở (root) + ngã rẽ (branch) + tận cùng phế nang (leaf)
/// Đường XANH LÁ = path nối các điểm
/// </summary>
[RequireComponent(typeof(MeshFilter))]
public class AirwayPathBuilder : MonoBehaviour
{
    [Header("Center theo mặt cắt ngang (về giữa lòng ống)")]
    [Tooltip("Bật kéo path về tâm lòng ống bằng mặt cắt (lan liên thông từ đỉnh gốc).")]
    [SerializeField] private bool useCrossCenter = true;
    [Tooltip("Số lần làm mịn path SAU khi center (khử jitter tâm mặt cắt) rồi mới hợp nhất.")]
    [SerializeField] private int pathPostSmooth = 4;
    [Tooltip("Chặn phình bán kính ống ở ngã rẽ: bán kính > factor × trung vị cục bộ sẽ bị kẹp. Nhỏ = chặn mạnh.")]
    [SerializeField] private float radiusOutlierFactor = 1.5f;

    [Header("Lan truyền ngược (gộp đường khi gần nhau)")]
    [Tooltip("Bước đi = stepFactor × kích thước mô hình. Nhỏ = mịn hơn.")]
    [SerializeField] private float stepFactor = 0.02f;
    [Header("Hợp nhất (lan truyền ngược, ngưỡng tăng dần)")]
    [Tooltip("Ngưỡng gộp tại PHẾ NANG (nhỏ) = mergeMinFactor × mô hình.")]
    [SerializeField] private float mergeMinFactor = 0.015f;
    [Tooltip("Ngưỡng gộp tại CUỐNG (lớn) = mergeMaxFactor × mô hình.")]
    [SerializeField] private float mergeMaxFactor = 0.08f;
    [Tooltip("Cửa sổ dung sai dọc trục (số mức) khi tìm điểm gần nhất trên path đối phương.")]
    [SerializeField] private int mergeWindow = 3;
    [Tooltip("Node tím chỉ đặt ở nơi >=2 con, mỗi con mang >= minBranchTips phế nang (mật độ hội tụ lớn).")]
    [SerializeField] private int minBranchTips = 3;
    [Tooltip("Ngưỡng TÁCH nhánh (dựng xuôi) = splitDistFactor × mô hình. Nhỏ = tách sớm/nhiều nhánh.")]
    [SerializeField] private float splitDistFactor = 0.02f;
    [Tooltip("Số mức nhìn trước để xác nhận tách (hai nhóm phải phân kỳ). Nhỏ = tách dễ hơn ở nhánh ngắn.")]
    [SerializeField] private int splitConfirm = 2;
    [Tooltip("Số mức lùi node rẽ về throat. NHỎ = node thấp/gần chỗ tách + ít web; LỚN = node cao vào throat.")]
    [SerializeField] private int nodeBack = 1;
    [Tooltip("Node rẽ đặt khi 2 nhánh rời nhau vượt hệ số này × bán kính ống. NHỎ = node gần NODE CHA; LỚN = ra phía đầu mút.")]
    [SerializeField] private float forkGap = 0.4f;
    [Tooltip("Gộp các node tím cách nhau < nodeMergeFactor × mô hình thành một (khử node sát nhau).")]
    [SerializeField] private float nodeMergeFactor = 0.02f;
    [Tooltip("Số lần làm mượt đường (Chaikin) để chuyển tiếp mềm tại điểm hợp nhất.")]
    [SerializeField] private int pathSmooth = 2;

    [Header("Tip Detection (phế nang)")]
    [Tooltip("Phạm vi tìm cực trị: 1 = chỉ hàng xóm sát (bắt nhiều tip hơn), 2 = rộng hơn (ít tip hơn).")]
    [SerializeField] private int localMaxRing = 1;
    [Tooltip("Gộp cực trị theo số bước trên lưới. Thấp hơn = giữ nhiều tip hơn (2-6).")]
    [SerializeField] private int mergeHops = 4;
    [Tooltip("Lọc cực trị giả trên thành ống: bán kính (bước lưới) để xét prominence.")]
    [SerializeField] private int prominenceHops = 6;
    [Tooltip("Ngưỡng prominence = tỉ lệ này × độ sâu tối đa. Cao hơn = lọc lỏng (giữ nhiều tip).")]
    [SerializeField] private float prominenceRatio = 0.02f;
    [Tooltip("LOẠI CỨNG tip gần đỉnh: bỏ tip có ĐƯỜNG ĐI (geodesic) tới cuống < pathRemoveFactor × đường đi sâu nhất.")]
    [SerializeField] private float pathRemoveFactor = 0.08f;
    [Tooltip("Đặt Empty GameObject tại các phế nang cần XÓA rồi kéo vào đây (xóa cứng thủ công).")]
    [SerializeField] private Transform[] hardRemoveMarkers;
    [Tooltip("Hoặc nhập thẳng tọa độ (world) các phế nang cần xóa.")]
    [SerializeField] private Vector3[] hardRemovePositions;
    [Tooltip("Bán kính xóa quanh mỗi marker/tọa độ = hệ số này × đường chéo mô hình.")]
    [SerializeField] private float hardRemoveMarkerRadius = 0.05f;
    [Tooltip("In tọa độ (world) từng phế nang ra Console để lấy vị trí cần xóa.")]
    [SerializeField] private bool logTipPositions = false;
    [Tooltip("LOẠI tip trên ống RỘNG (trục chính): bỏ tip có bán kính mặt cắt > hệ số này × đường chéo. 0 = tắt.")]
    [SerializeField] private float maxTubeRadiusFactor = 0.03f;

    [Header("Skeletonization (dùng ở bước sau)")]
    [Tooltip("Số vành đai chia theo chiều sâu.")]
    [SerializeField] private int bandCount = 80;
    [Tooltip("Gộp các cung cùng mặt cắt để node nằm ở TÂM ống.")]
    [SerializeField] private float mergeFactor = 1.5f;
    [Tooltip("Số lần làm mượt centerline. 0 = tắt.")]
    [SerializeField] private int smoothIterations = 0;

    [Header("Parameters")]
    [SerializeField] private float baseVelocity = 1.5f;
    [SerializeField] private float baseDensity = 1.0f;

    [Header("Auto Build")]
    [SerializeField] private bool autoBuildOnPlay = true;
    [SerializeField] private bool hideMeshOnPlay = false;
    [Tooltip("BAT: tu dung lai khi chinh Inspector (tien tinh chinh). TAT: Play chi dung 1 lan, khong cap nhat tren editor -> nhe hon.")]
    [SerializeField] private bool rebuildOnEdit = true;
    [Tooltip("BAT: trong khi Play, dung lai blue path dinh ky (theo mesh bien dang khi ho hap).")]
    [SerializeField] private bool rebuildDuringPlay = false;
    [Tooltip("Chu ky dung lai khi Play (giay).")]
    [SerializeField] private float rebuildInterval = 0.5f;
    [Tooltip("Ngan sach thoi gian moi khung khi dung lai NEN (ms). Nho hon = muot hon nhung dung lai lau hon.")]
    [SerializeField] private float buildBudgetMs = 8f;
    [Tooltip("In thoi gian moi lan dung lai (ms) de do do nang.")]
    [SerializeField] private bool logRebuildTime = true;

    [Header("Visualization (Gizmos)")]
    [Tooltip("Hiện BẢN ĐỒ NHIỆT trên bề mặt: màu theo khoảng cách đường đi tới đầu vào (kiểu radar mưa).")]
    [SerializeField] private bool showDistanceHeatmap = false;
    [Tooltip("Hiện ỨNG VIÊN sau lọc độ nổi, trước gom cụm (tô ĐỎ HỒNG).")]
    [SerializeField] private bool showCandidatesKept = false;
    [Tooltip("Hiện VÙNG PHẾ NANG cuối cùng sau gom cụm + lọc cứng (tô VÀNG).")]
    [SerializeField] private bool showFinalTipRegions = false;
    [Tooltip("Bán kính tô vùng quanh mỗi ứng viên (số bước lưới). Nhỏ = đốm gọn hơn.")]
    [SerializeField] private int regionHops = 1;
    [Tooltip("Hiện/ẩn các phế nang (cầu vàng).")]
    [SerializeField] private bool showAlveoli = true;
    [Tooltip("Hiện/ẩn các path gốc đã center (xanh lá).")]
    [SerializeField] private bool showGreenPaths = true;
    [Tooltip("Hiện/ẩn cây hợp nhất (xanh dương).")]
    [SerializeField] private bool showBluePaths = true;
    [SerializeField] private bool visualizeEdges = true;
    [SerializeField] private bool visualizeNodes = true;
    [SerializeField] private float nodeSize = 0.12f;
    [SerializeField] private Color nodeColor = Color.yellow;
    [SerializeField] private Color edgeColor = Color.green;

    // ===== Data Structures =====

    public class AirwayNode
    {
        public int id;
        public Vector3 position;
        public float radius;
        public int band;
        public List<int> childEdges = new List<int>();
        public int parentEdge = -1;
        public bool isRoot = false;
        public bool isLeaf = false;
        public bool isBranching = false;
    }

    public class AirwayEdge
    {
        public int id;
        public int fromNodeId;
        public int toNodeId;
        public List<Vector3> pathPoints = new List<Vector3>();
        public float length;
        public float diameter;
        public float velocity;
        public float density = 1f;
        public float branchingRatio = 1f;
    }

    public class AirwayNetwork
    {
        public List<AirwayNode> nodes = new List<AirwayNode>();
        public List<AirwayEdge> edges = new List<AirwayEdge>();
        public int rootNodeId = 0;
    }

    // ===== State =====

    private Mesh mesh;
    private List<Vector3> candidateRawPos = new List<Vector3>();   // cực đại địa phương (thô)
    private List<Vector3> candidateKeptPos = new List<Vector3>();  // sau lọc độ nổi
    private List<int> candidateRawIdx = new List<int>();
    private List<int> candidateKeptIdx = new List<int>();
    private Mesh heatmapMesh;
    private Material heatmapMaterial;
    private Material originalMaterial;
    private AirwayNetwork network = new AirwayNetwork();
    private List<List<Vector3>> airflowPaths = new List<List<Vector3>>(); // đường: cuống -> phế nang (đã center)
    private List<List<float>> airflowRadii = new List<List<float>>();     // bán kính ống dọc mỗi đường (song song)

    // ===== CÂY ĐIỀU HƯỚNG (flow tree): node -> các cạnh con, mỗi cạnh có polyline + bán kính =====
    public class FlowNode { public Vector3 pos; public bool isLeaf; public List<int> edges = new List<int>(); }
    public class FlowEdge { public Vector3[] pts; public float[] radii; public float[] radiiFill; public int to; public float caliber; } // radii=mean, radiiFill=min(rải hạt), caliber=bán kính đầu cạnh (r_i)
    private List<FlowNode> flowNodes = new List<FlowNode>();
    private List<FlowEdge> flowEdges = new List<FlowEdge>();

    // ===== API cho Giai đoạn 2 (điều khiển hạt) =====
    public IReadOnlyList<List<Vector3>> AirflowPaths => airflowPaths;
    public IReadOnlyList<List<float>> AirflowRadii => airflowRadii;
    public Vector3 InletPosition => (network != null && network.nodes.Count > 0) ? network.nodes[0].position : transform.position;
    public int PathCount => airflowPaths.Count;

    public IReadOnlyList<FlowNode> FlowNodes => flowNodes;
    public IReadOnlyList<FlowEdge> FlowEdges => flowEdges;
    public int FlowRoot => 0;

    // trả cả path và bán kính cùng lúc (giữ cho tương thích)
    public bool GetRandomPath(out List<Vector3> path, out List<float> radii)
    {
        if (airflowPaths.Count == 0) { path = null; radii = null; return false; }
        int i = UnityEngine.Random.Range(0, airflowPaths.Count);
        path = airflowPaths[i]; radii = airflowRadii[i]; return true;
    }
    private List<Vector3[]> treeEdges = new List<Vector3[]>();             // cạnh cây đã center (blue)
    private List<Vector3[]> treeEdgesRaw = new List<Vector3[]>();          // cạnh cây bám thành (green)
    private List<Vector3> branchPoints = new List<Vector3>();              // điểm rẽ nhánh
    private bool isBuilt = false;
    private int buildVersion = 0;
    public int BuildVersion => buildVersion;   // Stage-2 so version này để biết path đã dựng lại
    public event System.Action OnRebuilt;      // báo cho Stage-2 khi path dựng xong (đăng ký để cập nhật)
    private float _rebuildTimer = 0f;

    public AirwayNetwork Network => network;
    public bool IsBuilt => isBuilt;

    private void Awake()
    {
        if (autoBuildOnPlay)
            BuildNetwork();

        if (hideMeshOnPlay)
        {
            var mr = GetComponent<MeshRenderer>();
            if (mr != null) mr.enabled = false;
        }
    }

    [ContextMenu("Build Airway Network")]
    public void BuildNetwork()
    {
        var e = BuildRoutine(false);          // đồng bộ: chạy hết ngay (Awake/Editor/context-menu)
        while (e.MoveNext()) { }
    }

    private System.Diagnostics.Stopwatch _bsw;
    private bool _chunk;
    private System.Collections.IEnumerator BuildRoutine(bool chunk)
    {
        _chunk = chunk;
        _bsw = System.Diagnostics.Stopwatch.StartNew();
        var mf = GetComponent<MeshFilter>();
        // Khi Play: đọc mesh INSTANCE (phản ánh vertex animation runtime). Editor: sharedMesh.
        mesh = Application.isPlaying && mf != null && mf.mesh != null ? mf.mesh : (mf != null ? mf.sharedMesh : null);
        if (mesh == null)
        {
            Debug.LogError("[AirwayPathBuilder] Mesh not found!");
            yield break;
        }

        // --- 1. WELD: gộp các đỉnh trùng vị trí (fix mesh bị đứt gãy ở seam) ---
        Vector3[] rawLocal = mesh.vertices;
        int[] tris = mesh.triangles;

        // ==== biến dùng chung cho 2 pha (topology 1 lần / refresh mỗi hô hấp) ====
        int[] toWeld; int n; Vector3[] verts;
        int[] triA, triB, triC; List<int>[] vertTris; Dictionary<long, List<int>> edgeToTris;
        int inlet; int[] prev; List<int> tipVerts; float modelMax;

        if (!_topoReady)
        {

            var weldMap = new Dictionary<Vector3, int>();      // local pos -> weld id
            toWeld = new int[rawLocal.Length];                 // raw index -> weld id
            var weldPos = new List<Vector3>();                 // weld id -> world pos

            for (int i = 0; i < rawLocal.Length; i++)
            {
                Vector3 p = rawLocal[i];
                if (!weldMap.TryGetValue(p, out int wid))
                {
                    wid = weldPos.Count;
                    weldMap[p] = wid;
                    weldPos.Add(transform.TransformPoint(p));
                }
                toWeld[i] = wid;
            }

            n = weldPos.Count;
            verts = weldPos.ToArray();

            // adjacency trên weld ids
            HashSet<int>[] adj = new HashSet<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new HashSet<int>();
            for (int i = 0; i < tris.Length; i += 3)
            {
                int a = toWeld[tris[i]], b = toWeld[tris[i + 1]], c = toWeld[tris[i + 2]];
                adj[a].Add(b); adj[a].Add(c);
                adj[b].Add(a); adj[b].Add(c);
                adj[c].Add(a); adj[c].Add(b);
            }

            if (_chunk && _bsw.ElapsedMilliseconds >= buildBudgetMs) { yield return null; _bsw.Restart(); }

            // --- 2. Inlet = đỉnh cao nhất (Y lớn nhất) ---
            inlet = 0;
            for (int i = 1; i < n; i++)
                if (verts[i].y > verts[inlet].y) inlet = i;

            // --- 3. Dijkstra geodesic distance từ inlet ---
            float[] dist = Dijkstra(verts, adj, inlet, out prev);

            float maxDist = 0f;
            for (int i = 0; i < n; i++)
                if (dist[i] < float.MaxValue && dist[i] > maxDist) maxDist = dist[i];

            if (maxDist <= 0f)
            {
                Debug.LogError("[AirwayPathBuilder] Geodesic distance failed (mesh không liền mạch?).");
                yield break;
            }
            if (_chunk && _bsw.ElapsedMilliseconds >= buildBudgetMs) { yield return null; _bsw.Restart(); }   // nghỉ sau Dijkstra

            if (_chunk && _bsw.ElapsedMilliseconds >= buildBudgetMs) { yield return null; _bsw.Restart(); }

            // --- Liên thông tam giác (đỉnh→tri, cạnh→tri) cho cắt mặt cắt ---
            Vector3 boundsSize = mesh.bounds.size;
            modelMax = Mathf.Max(boundsSize.x, Mathf.Max(boundsSize.y, boundsSize.z)) * transform.lossyScale.x;

            int triCount = tris.Length / 3;
            triA = new int[triCount]; triB = new int[triCount]; triC = new int[triCount];
            vertTris = new List<int>[n];
            for (int i = 0; i < n; i++) vertTris[i] = new List<int>();
            edgeToTris = new Dictionary<long, List<int>>();

            for (int t = 0; t < triCount; t++)
            {
                int a = toWeld[tris[t * 3]], b = toWeld[tris[t * 3 + 1]], c = toWeld[tris[t * 3 + 2]];
                triA[t] = a; triB[t] = b; triC[t] = c;
                vertTris[a].Add(t); vertTris[b].Add(t); vertTris[c].Add(t);
                AddEdgeTri(edgeToTris, a, b, t);
                AddEdgeTri(edgeToTris, b, c, t);
                AddEdgeTri(edgeToTris, c, a, t);
            }

            if (_chunk && _bsw.ElapsedMilliseconds >= buildBudgetMs) { yield return null; _bsw.Restart(); }

            // --- 4. TÌM PHẾ NANG = local maxima của geodesic distance ---
            // Đi xuôi mỗi nhánh, dist tăng dần tới đỉnh rồi hết ống => đỉnh đó là đầu mút.
            // Một đỉnh là tip nếu dist của nó >= mọi hàng xóm trong 2-ring (không đỉnh nào xa hơn).
            List<int> tipCandidates = new List<int>();
            for (int v = 0; v < n; v++)
            {
                if (dist[v] >= float.MaxValue) continue;

                bool isMax = true;
                // ring 1: hàng xóm sát
                foreach (int u in adj[v])
                {
                    if (dist[u] > dist[v]) { isMax = false; break; }
                }
                // ring 2 (chỉ khi localMaxRing >= 2)
                if (isMax && localMaxRing >= 2)
                {
                    foreach (int u in adj[v])
                    {
                        foreach (int w in adj[u])
                            if (dist[w] > dist[v]) { isMax = false; break; }
                        if (!isMax) break;
                    }
                }

                if (isMax)
                    tipCandidates.Add(v);
            }

            // lưu ứng viên THÔ (cực đại địa phương, trước mọi phép lọc) để hiển thị
            candidateRawPos = new List<Vector3>();
            candidateRawIdx = new List<int>(tipCandidates);
            foreach (int v in tipCandidates) candidateRawPos.Add(verts[v]);

            // Lọc cực trị GIẢ trên thành ống bằng "prominence":
            // Tip thật là đầu nhánh -> đi prominenceHops bước quanh nó, KHÔNG hướng nào
            // dẫn tới đỉnh xa cuống hơn đáng kể. Cực trị giả trên thành thì có vùng lưới
            // xung quanh vẫn tăng dist -> loại.
            float promThreshold = 0f;
            {
                // ngưỡng = một phần nhỏ của khoảng cách sâu nhất
                promThreshold = maxDist * prominenceRatio;
            }

            List<int> realTips = new List<int>();
            foreach (int v in tipCandidates)
            {
                // BFS prominenceHops bước, tìm dist lớn nhất trong vùng lân cận
                float localMax = dist[v];
                Queue<(int idx, int depth)> pq2 = new Queue<(int, int)>();
                HashSet<int> seen2 = new HashSet<int> { v };
                pq2.Enqueue((v, 0));
                while (pq2.Count > 0)
                {
                    var (u, d) = pq2.Dequeue();
                    if (dist[u] > localMax) localMax = dist[u];
                    if (d >= prominenceHops) continue;
                    foreach (int w in adj[u])
                        if (seen2.Add(w))
                            pq2.Enqueue((w, d + 1));
                }

                // v là tip thật nếu nó gần như là đỉnh cao nhất trong vùng
                if (localMax - dist[v] <= promThreshold)
                    realTips.Add(v);
            }
            tipCandidates = realTips;

            // lưu ứng viên SAU lọc độ nổi (trước gom cụm) để hiển thị
            candidateKeptPos = new List<Vector3>();
            candidateKeptIdx = new List<int>(tipCandidates);
            foreach (int v in tipCandidates) candidateKeptPos.Add(verts[v]);

            // Gộp cực trị theo LIÊN THÔNG TRÊN LƯỚI (không theo khoảng cách Euclid).
            // Cực trị trên cùng 1 mũ ống cách nhau vài bước lưới -> gộp.
            // Tip ở 2 nhánh song song tuy gần trong không gian nhưng xa trên lưới -> giữ riêng.
            tipCandidates.Sort((a, b) => dist[b].CompareTo(dist[a]));

            var candSet = new HashSet<int>(tipCandidates);
            var used = new HashSet<int>();
            tipVerts = new List<int>();             // chỉ số đỉnh mesh của mỗi phế nang

            foreach (int c in tipCandidates)
            {
                if (used.Contains(c)) continue;

                // BFS quanh c trong mergeHops bước, đánh dấu mọi cực trị chạm được là "đã dùng"
                Queue<(int idx, int depth)> bfs = new Queue<(int, int)>();
                HashSet<int> seen = new HashSet<int> { c };
                bfs.Enqueue((c, 0));

                while (bfs.Count > 0)
                {
                    var (u, d) = bfs.Dequeue();
                    if (candSet.Contains(u)) used.Add(u);
                    if (d >= mergeHops) continue;
                    foreach (int w in adj[u])
                        if (seen.Add(w))
                            bfs.Enqueue((w, d + 1));
                }

                used.Add(c);
                tipVerts.Add(c); // c là cực trị mạnh nhất (dist lớn nhất) trong cụm
            }

            // LOẠI CỨNG: bỏ tip có ĐƯỜNG ĐI (geodesic) tới cuống ngắn - tip giả gần đỉnh đường thở
            // (không dùng khoảng cách Euclid: phế nang thật ở thùy trên có thể gần đỉnh trong không gian
            //  nhưng đường đi vẫn dài -> phải giữ)
            Vector3 wmn = verts[0], wmx = verts[0];
            for (int i = 1; i < n; i++) { wmn = Vector3.Min(wmn, verts[i]); wmx = Vector3.Max(wmx, verts[i]); }
            float diag = Vector3.Distance(wmn, wmx);   // đường chéo world (cho bán kính marker)

            float pathRemoveThresh = maxDist * pathRemoveFactor;
            int beforeInlet = tipVerts.Count;
            tipVerts.RemoveAll(tv => dist[tv] < pathRemoveThresh);
            Debug.Log($"[HardRemove path] pathRemoveFactor={pathRemoveFactor}, maxDist={maxDist:F3}, ngưỡng={pathRemoveThresh:F3}, XÓA={beforeInlet - tipVerts.Count}, còn={tipVerts.Count}");

            // LOẠI CỨNG THỦ CÔNG: xóa tip gần các marker (đặt Empty GameObject tại phế nang cần xóa)
            int removedByHand = 0;
            float mr = diag * hardRemoveMarkerRadius;
            if (hardRemoveMarkers != null)
                foreach (var m in hardRemoveMarkers)
                {
                    if (m == null) continue;
                    Vector3 mp = m.position;
                    removedByHand += tipVerts.RemoveAll(tv => Vector3.Distance(verts[tv], mp) < mr);
                }
            // hoặc nhập thẳng tọa độ (world) các phế nang cần xóa
            if (hardRemovePositions != null)
                foreach (var mp in hardRemovePositions)
                    removedByHand += tipVerts.RemoveAll(tv => Vector3.Distance(verts[tv], mp) < mr);

            if (logTipPositions)
            {
                var sb = new System.Text.StringBuilder("[Tip positions] (world)\n");
                for (int i = 0; i < tipVerts.Count; i++)
                    sb.AppendLine($"{i}: {verts[tipVerts[i]]}");
                Debug.Log(sb.ToString());
            }
            Debug.Log($"[HardRemove] Xóa thủ công: {removedByHand} tip, bán kính(world): {mr:F3}, còn: {tipVerts.Count} phế nang");

            // LOẠI TỰ ĐỘNG: bỏ tip nằm trên ỐNG RỘNG (trục chính) - bán kính mặt cắt lớn
            if (maxTubeRadiusFactor > 0f)
            {
                float rThresh = diag * maxTubeRadiusFactor;
                tipVerts.RemoveAll(tv =>
                {
                    // tiếp tuyến ~ hướng từ tip về đỉnh kề trên đường ngắn nhất
                    int pr = prev[tv];
                    Vector3 tan = pr >= 0 ? (verts[tv] - verts[pr]) : Vector3.up;
                    CrossSectionByBFS(tv, tan, verts, triA, triB, triC, vertTris, edgeToTris, out float rad);
                    return rad > rThresh;
                });
            }

            // --- TÔ MÀU BỀ MẶT: bản đồ nhiệt + vùng ứng viên phế nang (CHỈ pha topology) ---
            ApplySurfaceColors(mf, rawLocal.Length, toWeld, dist, maxDist, adj, tipVerts);

            // cache topology (chỉ số cố định khi hô hấp) để refresh nhanh
            c_toWeld = toWeld; c_n = n; c_inlet = inlet;
            c_triA = triA; c_triB = triB; c_triC = triC;
            c_vertTris = vertTris; c_edgeToTris = edgeToTris; c_prev = prev; c_tipVerts = tipVerts;
            _topoReady = true;
        }
        else
        {
            // ==== REFRESH: topology giữ nguyên, chỉ đọc lại VỊ TRÍ đỉnh mới ====
            n = c_n; toWeld = c_toWeld; inlet = c_inlet;
            triA = c_triA; triB = c_triB; triC = c_triC;
            vertTris = c_vertTris; edgeToTris = c_edgeToTris; prev = c_prev; tipVerts = c_tipVerts;
            verts = new Vector3[n];
            for (int i = 0; i < rawLocal.Length; i++) verts[toWeld[i]] = transform.TransformPoint(rawLocal[i]);
            Vector3 bs2 = mesh.bounds.size;
            modelMax = Mathf.Max(bs2.x, Mathf.Max(bs2.y, bs2.z)) * transform.lossyScale.x;
        }

        if (_chunk && _bsw.ElapsedMilliseconds >= buildBudgetMs) { yield return null; _bsw.Restart(); }

        // --- 5. Tạo node: root (đầu cuống) + tips (phế nang) ---
        network = new AirwayNetwork();

        network.nodes.Add(new AirwayNode
        {
            id = 0,
            position = verts[inlet],
            radius = 0.01f,
            isRoot = true
        });
        network.rootNodeId = 0;

        foreach (int tv in tipVerts)
        {
            network.nodes.Add(new AirwayNode
            {
                id = network.nodes.Count,
                position = verts[tv],
                radius = 0.01f,
                isLeaf = true
            });
        }

        // --- 6. Đánh dấu các đỉnh nằm trên đường đi (cây đường-đi-ngắn-nhất) ---
        bool[] onPath = new bool[n];
        foreach (int tv in tipVerts)
        {
            int cur = tv, guard = 0;
            while (cur != -1 && guard < n + 1)
            {
                onPath[cur] = true;
                if (cur == inlet) break;
                cur = prev[cur];
                guard++;
            }
        }

        // --- 10. (green = các path đã center, dựng sau khi có tipRealPath) ---
        treeEdgesRaw = new List<Vector3[]>();

        // --- 11. LAN TRUYỀN NGƯỢC: đi từng bước từ phế nang về cuống, gộp khi gần nhau ---
        // Chia mỗi đường theo mức = khoảng cách tới cuống (bước ds). Ở mỗi mức, điểm của
        // các phế nang khác nhau mà gần nhau (< mergeDist) => gộp 1 node. Xuôi về cuống
        // chúng dùng chung node => đường tự hợp nhất; chỗ >=2 luồng nhập = ngã rẽ.
        float ds = Mathf.Max(modelMax * stepFactor, 1e-5f);
        float mergeMin = modelMax * mergeMinFactor;
        float mergeMax = modelMax * mergeMaxFactor;

        // với mỗi phế nang: dựng đường (tip..root) đã center
        var tipRealPath = new List<List<Vector3>>();             // [tipIndex] đường thật (tip..root)
        var tipDToRoot = new List<float[]>();                    // [tipIndex] khoảng cách tới cuống
        var tipRadius = new List<List<float>>();                 // [tipIndex] bán kính ống dọc đường
        var tipRadiusMin = new List<List<float>>();              // [tipIndex] bán kính ngắn nhất (rải hạt)
        int maxLevel = 0;

        for (int ti = 0; ti < tipVerts.Count; ti++)
        {
            if (_chunk && _bsw.ElapsedMilliseconds >= buildBudgetMs) { yield return null; _bsw.Restart(); }   // nghỉ giữa các tip (mặt cắt)
            List<int> PV = new List<int>();     // chỉ số đỉnh (trên thành ống)
            int cur = tipVerts[ti], guard = 0;
            while (cur != -1 && guard < n + 1)
            {
                PV.Add(cur);
                if (cur == inlet) break;
                cur = prev[cur];
                guard++;
            }

            List<Vector3> P = new List<Vector3>(PV.Count);
            List<float> Rad = new List<float>(PV.Count);
            List<float> RadMin = new List<float>(PV.Count);
            for (int k = 0; k < PV.Count; k++)
            {
                if (useCrossCenter)
                {
                    // tiếp tuyến từ 2 đỉnh kề trên đường (đỉnh gốc trên thành)
                    Vector3 a = verts[PV[Mathf.Max(0, k - 1)]];
                    Vector3 b = verts[PV[Mathf.Min(PV.Count - 1, k + 1)]];
                    Vector3 tan = b - a;
                    // mặt cắt -> tâm + bán kính trung bình (rad) + bán kính ngắn nhất (radMin)
                    P.Add(CrossSectionByBFS(PV[k], tan, verts, triA, triB, triC, vertTris, edgeToTris, out float rad, out float radMin));
                    Rad.Add(rad); RadMin.Add(radMin);
                }
                else { P.Add(verts[PV[k]]); Rad.Add(0f); RadMin.Add(0f); }
            }

            // CHỮA TÂM ở ngã ba: chỗ bán kính vọt lên (mặt cắt bắt cả 2 ống) -> tâm méo,
            // thay bằng nội suy từ các điểm sạch lân cận (khử swerve/bắt chéo ở carina)
            P = RepairCenterAtJunctions(P, Rad, radiusOutlierFactor);

            // làm mịn path đã center (khử jitter của tâm mặt cắt) trước khi hợp nhất
            P = SmoothPath(P, pathPostSmooth);
            Rad = ClampRadiusOutliers(Rad, radiusOutlierFactor); // chặn phình ở ngã rẽ
            Rad = SmoothScalar(Rad, pathPostSmooth);
            RadMin = SmoothScalar(RadMin, pathPostSmooth);       // bán kính rải hạt (không tràn)

            // khoảng cách tới cuống dọc đường
            float[] dToRoot = new float[P.Count];
            dToRoot[P.Count - 1] = 0f;
            for (int k = P.Count - 2; k >= 0; k--)
                dToRoot[k] = dToRoot[k + 1] + Vector3.Distance(P[k], P[k + 1]);

            tipRealPath.Add(P);
            tipDToRoot.Add(dToRoot);
            tipRadius.Add(Rad);
            tipRadiusMin.Add(RadMin);

            int mxL = Mathf.FloorToInt(dToRoot[0] / ds);
            if (mxL > maxLevel) maxLevel = mxL;
        }

        // đường khí (cuống -> phế nang) từ path đã center, cho Giai đoạn 2
        airflowPaths = new List<List<Vector3>>();
        airflowRadii = new List<List<float>>();
        treeEdgesRaw = new List<Vector3[]>();
        for (int ti = 0; ti < tipRealPath.Count; ti++)
        {
            var rp = new List<Vector3>(tipRealPath[ti]);
            rp.Reverse();
            airflowPaths.Add(rp);

            var rr = new List<float>(tipRadius[ti]);
            rr.Reverse();
            airflowRadii.Add(rr);

            treeEdgesRaw.Add(tipRealPath[ti].ToArray());   // green = path gốc đã center
        }
        int brokenPaths = 0;

        // build mảng vị trí + bán kính theo mức cho mỗi phế nang
        var tipMaxLevel = new int[tipVerts.Count];
        var tipPosArr = new List<Vector3[]>();
        var tipRadArr = new List<float[]>();
        var tipRadMinArr = new List<float[]>();
        var tipsByMax = new Dictionary<int, List<int>>();
        for (int ti = 0; ti < tipVerts.Count; ti++)
        {
            var P = tipRealPath[ti];
            var Rd = tipRadius[ti];
            var RdM = tipRadiusMin[ti];
            var dR = tipDToRoot[ti];               // giảm dần: dR[0]=xa nhất, dR[cuối]=0
            int mx = Mathf.FloorToInt(dR[0] / ds);
            tipMaxLevel[ti] = mx;

            Vector3[] arr = new Vector3[mx + 1];
            float[] rarr = new float[mx + 1];
            float[] rmin = new float[mx + 1];
            int seg = 0;
            for (int L = mx; L >= 0; L--)   // từ phế nang (dt lớn) về cuống (dt=0), seg tiến đúng chiều
            {
                float dt = L * ds;
                while (seg < P.Count - 2 && dR[seg + 1] > dt) seg++;
                float denom = dR[seg] - dR[seg + 1];
                float frac = denom > 1e-9f ? Mathf.Clamp01((dR[seg] - dt) / denom) : 0f;
                arr[L] = Vector3.Lerp(P[seg], P[seg + 1], frac);
                rarr[L] = Mathf.Lerp(Rd[seg], Rd[seg + 1], frac);
                rmin[L] = Mathf.Lerp(RdM[seg], RdM[seg + 1], frac);
            }
            tipPosArr.Add(arr);
            tipRadArr.Add(rarr);
            tipRadMinArr.Add(rmin);

            if (!tipsByMax.TryGetValue(mx, out var lst)) { lst = new List<int>(); tipsByMax[mx] = lst; }
            lst.Add(ti);
        }

        if (_chunk && _bsw.ElapsedMilliseconds >= buildBudgetMs) { yield return null; _bsw.Restart(); }   // nghỉ trước dựng cây

        // ===== DỰNG XUÔI TỪ CUỐNG RA (đệ quy), đặt node rẽ nhánh đúng chỗ path tách =====
        // Bó path đi từ cuống ra; vị trí node = TRUNG BÌNH các path trong bó ở cùng độ sâu.
        // Khi bó tách thành >=2 nhóm (path xa nhau > ngưỡng) -> node rẽ nhánh (cha) -> đệ quy.
        // Tới phế nang (bó còn 1 path) -> leaf, dừng.
        float splitDist = modelMax * splitDistFactor;

        treeEdges = new List<Vector3[]>();
        branchPoints = new List<Vector3>();
        flowNodes = new List<FlowNode>();
        flowEdges = new List<FlowEdge>();

        var allTips = new List<int>();
        for (int i = 0; i < tipVerts.Count; i++) allTips.Add(i);

        Vector3 rootPos = GroupMean(allTips, 0, tipPosArr, tipMaxLevel);
        float rootR = GroupRadius(allTips, 0, tipRadArr, tipMaxLevel);   // bán kính khí quản (tham chiếu)
        network.nodes[0].position = rootPos;
        flowNodes.Add(new FlowNode { pos = rootPos, isLeaf = false }); // node 0 = cuống

        // stack: (bó tip, mức bắt đầu, id node cha trong flow tree)
        var stack = new Stack<(List<int> grp, int startL, int parentId)>();
        stack.Push((allTips, 0, 0));

        int guardOuter = 0;
        while (stack.Count > 0 && guardOuter++ < 100000)
        {
            var (grp, startL, parentId) = stack.Pop();
            Vector3 parentPos = flowNodes[parentId].pos;

            int L = startL;
            var levelPts = new List<Vector3> { parentPos };
            var levelRad = new List<float> { GroupRadius(grp, startL, tipRadArr, tipMaxLevel) };
            var levelRadMin = new List<float> { GroupRadius(grp, startL, tipRadMinArr, tipMaxLevel) };

            while (true)
            {
                // bó chỉ còn 1 path -> đi nốt tới phế nang (leaf)
                if (grp.Count == 1)
                {
                    int ti = grp[0];
                    for (int l = L + 1; l <= tipMaxLevel[ti]; l++) { levelPts.Add(tipPosArr[ti][l]); levelRad.Add(tipRadArr[ti][l]); levelRadMin.Add(tipRadMinArr[ti][l]); }
                    levelPts.Add(tipRealPath[ti][0]); levelRad.Add(tipRadArr[ti][tipMaxLevel[ti]]); levelRadMin.Add(tipRadMinArr[ti][tipMaxLevel[ti]]);
                    int leafId = AddFlowNode(tipRealPath[ti][0], true);
                    AddFlowEdge(parentId, leafId, levelPts, levelRad, levelRadMin);
                    // leaf = ống đơn từ ngã rẽ tới phế nang -> BÁM SÁT median (đã trơn sẵn), làm mượt tối thiểu
                    treeEdges.Add(Chaikin(levelPts, 1).ToArray());
                    break;
                }

                // các tip ĐÃ TỚI ĐẦU MÚT tại mức L -> rụng thành phế nang; phần còn lại đi chung tiếp
                var ending = new List<int>();
                var cont = new List<int>();
                foreach (int ti in grp)
                    if (tipMaxLevel[ti] <= L) ending.Add(ti); else cont.Add(ti);

                if (ending.Count > 0)
                {
                    // node hub đặt tại FORK - nơi tip rụng thật sự tách khỏi bó (giống case split, gần cha)
                    var egroups = new List<List<int>>();
                    foreach (int ti in ending) egroups.Add(new List<int> { ti });
                    if (cont.Count > 0) egroups.Add(cont);
                    int eFork = egroups.Count >= 2 ? FindForkLevel(egroups, startL, L, tipPosArr, tipRadArr, tipMaxLevel, forkGap) : Mathf.Max(startL, L - nodeBack);
                    int bLev = Mathf.Clamp(eFork, startL, L);
                    Vector3 hub = GroupMean(grp, bLev, tipPosArr, tipMaxLevel);
                    int keepE = bLev - startL + 1; if (keepE < 1) keepE = 1;
                    if (keepE < levelPts.Count) { levelPts.RemoveRange(keepE, levelPts.Count - keepE); levelRad.RemoveRange(keepE, levelRad.Count - keepE); levelRadMin.RemoveRange(keepE, levelRadMin.Count - keepE); }
                    levelPts.Add(hub); levelRad.Add(GroupRadius(grp, bLev, tipRadArr, tipMaxLevel)); levelRadMin.Add(GroupRadius(grp, bLev, tipRadMinArr, tipMaxLevel));
                    int hubId = AddFlowNode(hub, false);
                    AddFlowEdge(parentId, hubId, levelPts, levelRad, levelRadMin);
                    treeEdges.Add(Chaikin(levelPts, pathSmooth + 1).ToArray());
                    // Chỉ đánh dấu node rẽ nếu đầu mút peel-off ở ống ĐỦ HẸP (nhánh thật),
                    // KHÔNG tô trên khí quản/phế quản chính rộng (đầu mút giả path ngắn).
                    if (GroupRadius(grp, bLev, tipRadArr, tipMaxLevel) < rootR * 0.6f)
                        branchPoints.Add(hub);

                    // mỗi phế nang đã tới mút -> đệ quy singleton từ throat (leaf sẽ bám green của nó)
                    foreach (int ti in ending)
                        stack.Push((new List<int> { ti }, bLev, hubId));
                    // phần còn lại tiếp tục đi chung từ throat
                    if (cont.Count >= 1)
                        stack.Push((cont, bLev, hubId));
                    break;
                }

                int nextL = L + 1;

                var groups = SplitGroup(grp, nextL, tipPosArr, tipMaxLevel, splitDist);
                bool realSplit = groups.Count > 1 &&
                    ConfirmSplit(groups, nextL, nextL + splitConfirm, tipPosArr, tipMaxLevel, splitDist);

                if (!realSplit)
                {
                    levelPts.Add(GroupMean(grp, nextL, tipPosArr, tipMaxLevel));
                    levelRad.Add(GroupRadius(grp, nextL, tipRadArr, tipMaxLevel));
                    levelRadMin.Add(GroupRadius(grp, nextL, tipRadMinArr, tipMaxLevel));
                    L = nextL;
                    continue;
                }

                // Node tại FORK; mọi nhánh con BÁM MEDIAN từ fork -> mềm (theo ống cong),
                // node đúng ngã ba, không web (tách ngay từ fork nên không có đoạn chồng lấn trước đó).
                int forkLevel = FindForkLevel(groups, startL, L, tipPosArr, tipRadArr, tipMaxLevel, forkGap);
                int branchLevel = Mathf.Clamp(forkLevel, startL, L);
                Vector3 branch = GroupMean(grp, branchLevel, tipPosArr, tipMaxLevel);

                int keep = branchLevel - startL + 1;
                if (keep < 1) keep = 1;
                if (keep < levelPts.Count) { levelPts.RemoveRange(keep, levelPts.Count - keep); levelRad.RemoveRange(keep, levelRad.Count - keep); levelRadMin.RemoveRange(keep, levelRadMin.Count - keep); }
                levelPts.Add(branch); levelRad.Add(GroupRadius(grp, branchLevel, tipRadArr, tipMaxLevel)); levelRadMin.Add(GroupRadius(grp, branchLevel, tipRadMinArr, tipMaxLevel));

                int branchId = AddFlowNode(branch, false);
                AddFlowEdge(parentId, branchId, levelPts, levelRad, levelRadMin);
                treeEdges.Add(Chaikin(levelPts, pathSmooth + 1).ToArray());
                branchPoints.Add(branch);

                // Mọi nhánh con bám median từ fork -> mềm, theo ống cong
                foreach (var sub in groups)
                    stack.Push((sub, branchLevel, branchId));
                break;
            }
        }


        // ===== GỘP các node tím quá gần nhau thành một (khử node sát nhau do lùi mức) =====
        float nodeMergeDist = modelMax * nodeMergeFactor;
        if (branchPoints.Count > 0 && nodeMergeDist > 0f)
        {
            int bn = branchPoints.Count;
            int[] uf = new int[bn];
            for (int i = 0; i < bn; i++) uf[i] = i;
            for (int a = 0; a < bn; a++)
                for (int b = a + 1; b < bn; b++)
                    if (Vector3.Distance(branchPoints[a], branchPoints[b]) < nodeMergeDist)
                        Union(uf, a, b);

            var cSum = new Dictionary<int, Vector3>();
            var cCnt = new Dictionary<int, int>();
            for (int i = 0; i < bn; i++)
            {
                int r = Find(uf, i);
                cSum[r] = (cSum.TryGetValue(r, out var sv) ? sv : Vector3.zero) + branchPoints[i];
                cCnt[r] = (cCnt.TryGetValue(r, out var cv) ? cv : 0) + 1;
            }
            var merged = new List<Vector3>();
            foreach (var r in cSum.Keys) merged.Add(cSum[r] / cCnt[r]);

            // snap đầu/cuối mỗi cạnh về node gộp gần nhất (để cạnh vẫn nối vào node)
            foreach (var e in treeEdges)
            {
                SnapToMerged(ref e[0], merged, nodeMergeDist);
                SnapToMerged(ref e[e.Length - 1], merged, nodeMergeDist);
            }
            branchPoints = merged;
        }

        // vị trí chấm phế nang (đã center)
        for (int i = 0; i < tipVerts.Count; i++)
            network.nodes[i + 1].position = tipRealPath[i][0];

        isBuilt = true;
        buildVersion++;   // hook cho Stage-2 biết path đã đổi
        _bandCacheValid = true;   // dải tam giác đã lưu -> rebuild sau tái dùng (nhanh)
        OnRebuilt?.Invoke();      // THÔNG BÁO: path đã dựng xong -> Stage-2 cập nhật theo
        Debug.Log($"[AirwayPathBuilder] Phế nang: {tipVerts.Count}, Node tím: {branchPoints.Count}, Cạnh: {treeEdges.Count}");
    }

    // ===== Helpers =====

    // Tô bề mặt: bản đồ nhiệt theo khoảng cách trắc địa + tô nổi các VÙNG ứng viên phế nang.
    // Dùng vertex color: GPU nội suy giữa các đỉnh -> bề mặt lên màu liên tục.
    private void ApplySurfaceColors(MeshFilter mf, int rawCount, int[] toWeld, float[] dist, float maxDist,
                                    HashSet<int>[] adj, List<int> tipVerts)
    {
        var mr = GetComponent<MeshRenderer>();
        if (mf == null || mr == null) return;

        bool anyOn = showDistanceHeatmap || showCandidatesKept || showFinalTipRegions;
        if (!anyOn)
        {
            if (originalMaterial != null && mr.sharedMaterial == heatmapMaterial)
                mr.sharedMaterial = originalMaterial;
            return;
        }

        if (originalMaterial == null && mr.sharedMaterial != heatmapMaterial)
            originalMaterial = mr.sharedMaterial;

        if (heatmapMesh == null)
        {
            heatmapMesh = Instantiate(mf.sharedMesh);
            heatmapMesh.name = mf.sharedMesh.name + "_heatmap";
        }

        int n = adj.Length;

        // Lan vùng quanh mỗi ứng viên (BFS regionHops bước) -> nhãn vùng để tô nổi
        int[] mark = new int[n];   // 0 = không, 2 = sau lọc độ nổi, 3 = phế nang cuối
        if (showCandidatesKept) MarkRegions(candidateKeptIdx, adj, mark, 2);
        if (showFinalTipRegions) MarkRegions(tipVerts, adj, mark, 3);

        Color[] cols = new Color[rawCount];
        for (int i = 0; i < rawCount; i++)
        {
            int w = toWeld[i];

            Color baseCol;
            if (showDistanceHeatmap)
            {
                float d = dist[w];
                float t = (d >= float.MaxValue) ? 0f : Mathf.Clamp01(d / maxDist);
                baseCol = RainRadarColor(t);
            }
            else baseCol = new Color(0.65f, 0.72f, 0.78f); // nền xám khi tắt heatmap

            // vùng ứng viên tô đè bằng màu tương phản
            switch (mark[w])
            {
                case 2: baseCol = new Color(1f, 0.05f, 0.25f); break;    // sau lọc độ nổi - ĐỎ HỒNG
                case 3: baseCol = new Color(1f, 0.95f, 0f); break;       // phế nang cuối - VÀNG
            }
            cols[i] = baseCol;
        }

        heatmapMesh.vertices = mf.sharedMesh.vertices;
        heatmapMesh.triangles = mf.sharedMesh.triangles;
        heatmapMesh.normals = mf.sharedMesh.normals;
        heatmapMesh.colors = cols;
        mf.mesh = heatmapMesh;

        if (heatmapMaterial == null)
        {
            var sh = Shader.Find("Airway/VertexColorHeatmap");
            if (sh == null) { Debug.LogWarning("[AirwayPathBuilder] Chưa tìm thấy shader Airway/VertexColorHeatmap"); return; }
            heatmapMaterial = new Material(sh) { name = "AirwayHeatmapMat" };
        }
        mr.sharedMaterial = heatmapMaterial;
    }

    // Lan vùng quanh mỗi đỉnh trong seeds regionHops bước, gán nhãn tag
    private void MarkRegions(List<int> seeds, HashSet<int>[] adj, int[] mark, int tag)
    {
        if (seeds == null) return;
        var q = new Queue<(int v, int d)>();
        var seen = new HashSet<int>();
        foreach (int s in seeds) if (seen.Add(s)) q.Enqueue((s, 0));
        while (q.Count > 0)
        {
            var (v, d) = q.Dequeue();
            mark[v] = tag;
            if (d >= regionHops) continue;
            foreach (int u in adj[v])
                if (seen.Add(u)) q.Enqueue((u, d + 1));
        }
    }

    // Bảng màu radar mưa: xanh dương (gần cuống) -> xanh lá -> vàng -> cam -> đỏ -> tím (phế nang)
    private Color RainRadarColor(float t)
    {
        t = Mathf.Clamp01(t);
        Color[] stops = {
            new Color(0.10f, 0.30f, 0.90f), // xanh dương
            new Color(0.10f, 0.75f, 0.95f), // xanh lơ
            new Color(0.15f, 0.80f, 0.25f), // xanh lá
            new Color(0.95f, 0.90f, 0.15f), // vàng
            new Color(1.00f, 0.50f, 0.00f), // cam
            new Color(0.90f, 0.05f, 0.05f), // đỏ
            new Color(0.60f, 0.00f, 0.00f), // đỏ đậm
            new Color(0.70f, 0.00f, 0.75f)  // tím (cực đại = phế nang)
        };
        float x = t * (stops.Length - 1);
        int i = Mathf.Min(Mathf.FloorToInt(x), stops.Length - 2);
        return Color.Lerp(stops[i], stops[i + 1], x - i);
    }

    // Làm mượt polyline bằng Chaikin corner-cutting (giữ 2 đầu)
    private List<Vector3> Chaikin(List<Vector3> pts, int iters)
    {
        var cur = pts;
        for (int it = 0; it < iters; it++)
        {
            if (cur.Count < 3) break;
            var nx = new List<Vector3>(cur.Count * 2);
            nx.Add(cur[0]);
            for (int i = 0; i < cur.Count - 1; i++)
            {
                Vector3 p = cur[i], q = cur[i + 1];
                nx.Add(p * 0.75f + q * 0.25f);
                nx.Add(p * 0.25f + q * 0.75f);
            }
            nx.Add(cur[cur.Count - 1]);
            cur = nx;
        }
        return cur;
    }

    // Chỗ bán kính là outlier (ngã ba: mặt cắt bắt cả 2 ống -> tâm bị kéo lệch) thì
    // thay TÂM bằng nội suy tuyến tính giữa hai điểm "sạch" gần nhất hai bên.
    private List<Vector3> RepairCenterAtJunctions(List<Vector3> P, List<float> r, float factor)
    {
        int n = P.Count;
        if (n < 5) return P;
        float maxR = 0f; foreach (var rr in r) if (rr > maxR) maxR = rr;
        float absGate = maxR * 0.35f;   // chỉ ngã ba RỘNG mới chữa; nhánh mảnh bỏ qua
        int half = 3;
        bool[] bad = new bool[n];
        var window = new List<float>();
        for (int i = 0; i < n; i++)
        {
            window.Clear();
            for (int j = Mathf.Max(0, i - half); j <= Mathf.Min(n - 1, i + half); j++) window.Add(r[j]);
            window.Sort();
            float med = window[window.Count / 2];
            if (med > 1e-6f && r[i] > med * factor && r[i] > absGate) bad[i] = true;
        }
        var outp = new List<Vector3>(P);
        int idx = 0;
        while (idx < n)
        {
            if (!bad[idx]) { idx++; continue; }
            int s = idx; while (idx < n && bad[idx]) idx++;
            int e = idx;                    // [s, e) là đoạn xấu
            int lo = s - 1, hi = e;         // điểm sạch hai bên
            Vector3 a = lo >= 0 ? P[lo] : P[Mathf.Min(hi, n - 1)];
            Vector3 b = hi < n ? P[hi] : P[Mathf.Max(lo, 0)];
            for (int k = s; k < e; k++)
            {
                float t = (hi - lo) > 0 ? (float)(k - lo) / (hi - lo) : 0.5f;
                outp[k] = Vector3.Lerp(a, b, t);
            }
        }
        return outp;
    }

    // Kẹp bán kính bất thường: điểm nào lớn hơn factor lần trung vị cửa sổ trượt thì hạ về đó.
    // Dùng để chặn phình bán kính ở ngã rẽ (mặt cắt bắt cả hai nhánh).
    private List<float> ClampRadiusOutliers(List<float> r, float factor)
    {
        int n = r.Count;
        if (n < 5) return r;
        var outp = new List<float>(r);
        int half = 3; // cửa sổ 7 điểm
        var window = new List<float>();
        for (int i = 0; i < n; i++)
        {
            window.Clear();
            for (int j = Mathf.Max(0, i - half); j <= Mathf.Min(n - 1, i + half); j++)
                window.Add(r[j]);
            window.Sort();
            float med = window[window.Count / 2];
            if (med > 1e-6f && r[i] > med * factor)
                outp[i] = med * factor;
        }
        return outp;
    }

    // Làm mượt dãy vô hướng (Laplacian, giữ 2 đầu) - cho bán kính dọc path
    private List<float> SmoothScalar(List<float> vals, int iters)
    {
        if (vals.Count < 3 || iters <= 0) return vals;
        var cur = new List<float>(vals);
        for (int it = 0; it < iters; it++)
        {
            var nx = new List<float>(cur.Count);
            nx.Add(cur[0]);
            for (int i = 1; i < cur.Count - 1; i++)
                nx.Add((cur[i - 1] + cur[i] * 2f + cur[i + 1]) * 0.25f);
            nx.Add(cur[cur.Count - 1]);
            cur = nx;
        }
        return cur;
    }

    // Làm mịn polyline bằng Laplacian (giữ 2 đầu)
    private List<Vector3> SmoothPath(List<Vector3> pts, int iters)
    {
        if (pts.Count < 3 || iters <= 0) return pts;
        var cur = new List<Vector3>(pts);
        for (int it = 0; it < iters; it++)
        {
            var nx = new List<Vector3>(cur.Count);
            nx.Add(cur[0]);
            for (int i = 1; i < cur.Count - 1; i++)
                nx.Add((cur[i - 1] + cur[i] * 2f + cur[i + 1]) * 0.25f);
            nx.Add(cur[cur.Count - 1]);
            cur = nx;
        }
        return cur;
    }

    // Tiếp tuyến của path tại điểm k (hướng đi của đường)
    private Vector3 PathTangent(List<Vector3> P, int k)
    {
        if (P.Count < 2) return Vector3.up;
        Vector3 a = P[Mathf.Max(0, k - 1)];
        Vector3 b = P[Mathf.Min(P.Count - 1, k + 1)];
        Vector3 t = b - a;
        return t.sqrMagnitude > 1e-12f ? t.normalized : Vector3.up;
    }

    // Thêm tam giác vào bảng cạnh->tam giác
    private void AddEdgeTri(Dictionary<long, List<int>> map, int i0, int i1, int t)
    {
        long key = i0 < i1 ? ((long)i0 << 32) | (uint)i1 : ((long)i1 << 32) | (uint)i0;
        if (!map.TryGetValue(key, out var lst)) { lst = new List<int>(); map[key] = lst; }
        lst.Add(t);
    }

    // Tâm lòng ống tại đỉnh gốc s: LAN theo liên thông tam giác, bám các cạnh cắt mặt phẳng
    // pháp tuyến N quanh ống (vòng khép kín) -> trọng tâm GIAO ĐIỂM. Không nhảy sang nhánh khác.
    private Vector3 CrossSectionByBFS(int s, Vector3 tan, Vector3[] verts,
        int[] tA, int[] tB, int[] tC, List<int>[] vertTris, Dictionary<long, List<int>> edgeToTris)
    {
        return CrossSectionByBFS(s, tan, verts, tA, tB, tC, vertTris, edgeToTris, out _, out _);
    }

    private Vector3 CrossSectionByBFS(int s, Vector3 tan, Vector3[] verts,
        int[] tA, int[] tB, int[] tC, List<int>[] vertTris, Dictionary<long, List<int>> edgeToTris, out float radius)
    {
        return CrossSectionByBFS(s, tan, verts, tA, tB, tC, vertTris, edgeToTris, out radius, out _);
    }

    // radius = bán kính TRUNG BÌNH (cỡ ống, dùng cho ngã ba + caliber)
    // radiusMin = khoảng cách NGẮN NHẤT tâm->vòng (bán kính nội tiếp, dùng rải hạt để không tràn)
    // ===== CACHE dải tam giác mặt cắt (theo đỉnh gốc) để rebuild nhanh khi mesh biến dạng =====
    private Dictionary<int, int[]> _csBandCache = new Dictionary<int, int[]>();
    private bool _bandCacheValid = false;
    // ==== cache topology (chỉ số cố định khi hô hấp) ====
    private bool _topoReady = false;
    private int[] c_toWeld; private int c_n; private int c_inlet;
    private int[] c_triA, c_triB, c_triC;
    private List<int>[] c_vertTris;
    private Dictionary<long, List<int>> c_edgeToTris;
    private int[] c_prev;
    private List<int> c_tipVerts;
    public void ClearBandCache() { _csBandCache.Clear(); _bandCacheValid = false; _topoReady = false; }

    private Vector3 CrossSectionByBFS(int s, Vector3 tan, Vector3[] verts,
        int[] tA, int[] tB, int[] tC, List<int>[] vertTris, Dictionary<long, List<int>> edgeToTris,
        out float radius, out float radiusMin)
    {
        int[] band;
        if (_bandCacheValid && _csBandCache.TryGetValue(s, out band))
        {
            // TÁI DÙNG dải đã lưu -> chỉ tính lại giao điểm với vị trí mới (bỏ BFS)
            return CenterFromBand(band, verts[s], tan, verts, tA, tB, tC, out radius, out radiusMin);
        }
        // Lần đầu: BFS tìm dải, lưu cache
        band = FindBand(s, tan, verts, tA, tB, tC, vertTris, edgeToTris);
        _csBandCache[s] = band;
        return CenterFromBand(band, verts[s], tan, verts, tA, tB, tC, out radius, out radiusMin);
    }

    // BFS lan quanh ống theo cạnh cắt -> trả về danh sách tam giác của dải viền
    private int[] FindBand(int s, Vector3 tan, Vector3[] verts,
        int[] tA, int[] tB, int[] tC, List<int>[] vertTris, Dictionary<long, List<int>> edgeToTris)
    {
        Vector3 P = verts[s];
        Vector3 N = tan.sqrMagnitude > 1e-12f ? tan.normalized : Vector3.up;
        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int t in vertTris[s]) if (visited.Add(t)) queue.Enqueue(t);
        var edgeDone = new HashSet<long>();
        int guard = 0, maxTris = 4000;
        while (queue.Count > 0 && guard++ < maxTris)
        {
            int t = queue.Dequeue();
            int a = tA[t], b = tB[t], c = tC[t];
            float da = Vector3.Dot(verts[a] - P, N), db = Vector3.Dot(verts[b] - P, N), dc = Vector3.Dot(verts[c] - P, N);
            SpreadEdge(a, b, da, db, edgeDone, edgeToTris, visited, queue, t);
            SpreadEdge(b, c, db, dc, edgeDone, edgeToTris, visited, queue, t);
            SpreadEdge(c, a, dc, da, edgeDone, edgeToTris, visited, queue, t);
        }
        var arr = new int[visited.Count]; visited.CopyTo(arr); return arr;
    }
    private void SpreadEdge(int i0, int i1, float d0, float d1, HashSet<long> edgeDone,
        Dictionary<long, List<int>> edgeToTris, HashSet<int> visited, Queue<int> queue, int self)
    {
        if ((d0 < 0f) == (d1 < 0f)) return;
        long key = i0 < i1 ? ((long)i0 << 32) | (uint)i1 : ((long)i1 << 32) | (uint)i0;
        edgeDone.Add(key);
        if (edgeToTris.TryGetValue(key, out var nts))
            foreach (int nt in nts) if (nt != self && visited.Add(nt)) queue.Enqueue(nt);
    }

    // Tính TÂM + bán kính từ dải tam giác đã có (không BFS) - dùng vị trí verts hiện tại
    private Vector3 CenterFromBand(int[] band, Vector3 P, Vector3 tan, Vector3[] verts,
        int[] tA, int[] tB, int[] tC, out float radius, out float radiusMin)
    {
        radius = 0f; radiusMin = 0f;
        Vector3 N = tan.sqrMagnitude > 1e-12f ? tan.normalized : Vector3.up;
        var pts = new List<Vector3>(band.Length);
        var edgeDone = new HashSet<long>();
        foreach (int t in band)
        {
            int a = tA[t], b = tB[t], c = tC[t];
            Vector3 va = verts[a], vb = verts[b], vc = verts[c];
            float da = Vector3.Dot(va - P, N), db = Vector3.Dot(vb - P, N), dc = Vector3.Dot(vc - P, N);
            AddCross(a, b, va, vb, da, db, edgeDone, pts);
            AddCross(b, c, vb, vc, db, dc, edgeDone, pts);
            AddCross(c, a, vc, va, dc, da, edgeDone, pts);
        }
        if (pts.Count == 0) return P;
        Vector3 c0 = Vector3.zero; foreach (var p in pts) c0 += p; c0 /= pts.Count;
        float rsum = 0f, rmin = float.MaxValue;
        foreach (var p in pts) { float d = Vector3.Distance(p, c0); rsum += d; if (d < rmin) rmin = d; }
        radius = rsum / pts.Count; radiusMin = rmin;
        return c0;
    }
    private void AddCross(int i0, int i1, Vector3 v0, Vector3 v1, float d0, float d1, HashSet<long> edgeDone, List<Vector3> pts)
    {
        if ((d0 < 0f) == (d1 < 0f)) return;
        long key = i0 < i1 ? ((long)i0 << 32) | (uint)i1 : ((long)i1 << 32) | (uint)i0;
        if (!edgeDone.Add(key)) return;
        float tt = d0 / (d0 - d1);
        if (tt > 1e-4f && tt < 1f - 1e-4f) pts.Add(v0 + tt * (v1 - v0));
    }

    private Vector3 CrossSectionByBFS_OLD(int s, Vector3 tan, Vector3[] verts,
        int[] tA, int[] tB, int[] tC, List<int>[] vertTris, Dictionary<long, List<int>> edgeToTris,
        out float radius, out float radiusMin)
    {
        radius = 0f; radiusMin = 0f;
        Vector3 P = verts[s];
        Vector3 N = tan.sqrMagnitude > 1e-12f ? tan.normalized : Vector3.up;

        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int t in vertTris[s]) if (visited.Add(t)) queue.Enqueue(t);

        var pts = new List<Vector3>();
        var edgeDone = new HashSet<long>();
        int guard = 0, maxTris = 4000;

        while (queue.Count > 0 && guard++ < maxTris)
        {
            int t = queue.Dequeue();
            int a = tA[t], b = tB[t], c = tC[t];
            Vector3 va = verts[a], vb = verts[b], vc = verts[c];
            float da = Vector3.Dot(va - P, N), db = Vector3.Dot(vb - P, N), dc = Vector3.Dot(vc - P, N);

            ProcessEdge(a, b, va, vb, da, db, edgeDone, pts, edgeToTris, visited, queue, t);
            ProcessEdge(b, c, vb, vc, db, dc, edgeDone, pts, edgeToTris, visited, queue, t);
            ProcessEdge(c, a, vc, va, dc, da, edgeDone, pts, edgeToTris, visited, queue, t);
        }

        if (pts.Count == 0) return P;

        Vector3 c0 = Vector3.zero;
        foreach (var p in pts) c0 += p;
        c0 /= pts.Count;

        float rsum = 0f, rmin = float.MaxValue;
        foreach (var p in pts)
        {
            float d = Vector3.Distance(p, c0);
            rsum += d;
            if (d < rmin) rmin = d;
        }
        radius = rsum / pts.Count;
        radiusMin = rmin;

        return c0;
    }

    // Nếu cạnh cắt mặt phẳng: thêm giao điểm (nằm đúng trên mặt phẳng) + lan sang tam giác kề
    private void ProcessEdge(int i0, int i1, Vector3 v0, Vector3 v1, float d0, float d1,
        HashSet<long> edgeDone, List<Vector3> pts, Dictionary<long, List<int>> edgeToTris,
        HashSet<int> visited, Queue<int> queue, int self)
    {
        if ((d0 < 0f) == (d1 < 0f)) return; // không cắt

        long key = i0 < i1 ? ((long)i0 << 32) | (uint)i1 : ((long)i1 << 32) | (uint)i0;
        if (edgeDone.Add(key))
        {
            float tt = d0 / (d0 - d1);
            // bỏ giao điểm suy biến rơi ngay trên đỉnh gốc (trên thành) -> tránh kéo tâm lệch
            if (tt > 1e-4f && tt < 1f - 1e-4f)
                pts.Add(v0 + tt * (v1 - v0));
        }
        // lan sang tam giác kề qua cạnh cắt (đi trọn vòng quanh ống)
        if (edgeToTris.TryGetValue(key, out var nts))
            foreach (int nt in nts)
                if (nt != self && visited.Add(nt))
                    queue.Enqueue(nt);
    }

    // Khoảng cách từ điểm p tới đoạn thẳng [a,b]
    private float PointToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float t = Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f);
        t = Mathf.Clamp01(t);
        return Vector3.Distance(p, a + t * ab);
    }

    // Trích đoạn path (tip..root) có khoảng-cách-tới-cuống trong [dLow, dHigh]
    // Trả về theo chiều từ dHigh (gần phế nang) -> dLow (gần cuống)
    private List<Vector3> ExtractSubPath(List<Vector3> P, float[] dR, float dHigh, float dLow)
    {
        var outp = new List<Vector3>();
        if (P.Count < 2) return outp;
        if (dHigh < dLow) { float t = dHigh; dHigh = dLow; dLow = t; }

        // điểm đầu tại đúng dHigh (nội suy)
        outp.Add(InterpAtDist(P, dR, dHigh));
        // các đỉnh gốc nằm trong (dLow, dHigh), theo index tăng (dR giảm) = dHigh->dLow
        for (int k = 0; k < P.Count; k++)
            if (dR[k] < dHigh && dR[k] > dLow)
                outp.Add(P[k]);
        // điểm cuối tại đúng dLow
        outp.Add(InterpAtDist(P, dR, dLow));
        return outp;
    }

    private Vector3 InterpAtDist(List<Vector3> P, float[] dR, float dt)
    {
        if (dt >= dR[0]) return P[0];
        if (dt <= dR[P.Count - 1]) return P[P.Count - 1];
        for (int k = 0; k < P.Count - 1; k++)
            if (dR[k] >= dt && dR[k + 1] <= dt)
            {
                float denom = dR[k] - dR[k + 1];
                float frac = denom > 1e-9f ? (dR[k] - dt) / denom : 0f;
                return Vector3.Lerp(P[k], P[k + 1], frac);
            }
        return P[P.Count - 1];
    }

    // Kéo điểm về node gộp gần nhất nếu trong ngưỡng (để cạnh nối vào node đã gộp)
    private void SnapToMerged(ref Vector3 p, List<Vector3> merged, float thresh)
    {
        float best = thresh; int bi = -1;
        for (int i = 0; i < merged.Count; i++)
        {
            float d = Vector3.Distance(p, merged[i]);
            if (d < best) { best = d; bi = i; }
        }
        if (bi >= 0) p = merged[bi];
    }

    // Thêm node vào flow tree, trả id
    private int AddFlowNode(Vector3 pos, bool isLeaf)
    {
        flowNodes.Add(new FlowNode { pos = pos, isLeaf = isLeaf });
        return flowNodes.Count - 1;
    }

    // Thêm cạnh from->to; LÀM MƯỢT polyline + bán kính (Chaikin) để hạt chạy mượt, hết cứng ở ngã rẽ
    private void AddFlowEdge(int from, int to, List<Vector3> pts, List<float> radii, List<float> radiiMin)
    {
        ChaikinBoth(pts, radii, pathSmooth, out Vector3[] sp, out float[] sr);
        // làm mượt bán kính min theo cùng số lần Chaikin (khớp số điểm với sp)
        var srmin = ChaikinScalar(radiiMin, pathSmooth);
        if (srmin.Length != sp.Length) srmin = Resample(srmin, sp.Length);
        // caliber = bán kính tại ĐẦU cạnh (chỗ nối vào nút) = r_i để chia lưu lượng theo tiết diện;
        // KHÔNG lấy trung bình dọc cạnh vì ống thon dần.
        float cal = sr.Length > 0 ? sr[0] : 0f;
        var e = new FlowEdge { pts = sp, radii = sr, radiiFill = srmin, to = to, caliber = cal };
        int id = flowEdges.Count;
        flowEdges.Add(e);
        flowNodes[from].edges.Add(id);
    }

    // Chaikin cho một dãy vô hướng (cùng luật với ChaikinBoth để khớp số điểm)
    private float[] ChaikinScalar(List<float> rad, int iters)
    {
        var cr = new List<float>(rad);
        for (int it = 0; it < iters; it++)
        {
            if (cr.Count < 3) break;
            var nr = new List<float>(cr.Count * 2);
            nr.Add(cr[0]);
            for (int i = 0; i < cr.Count - 1; i++)
            {
                nr.Add(cr[i] * 0.75f + cr[i + 1] * 0.25f);
                nr.Add(cr[i] * 0.25f + cr[i + 1] * 0.75f);
            }
            nr.Add(cr[cr.Count - 1]);
            cr = nr;
        }
        return cr.ToArray();
    }

    private float[] Resample(float[] a, int n)
    {
        var o = new float[n];
        if (a.Length == 0) return o;
        for (int i = 0; i < n; i++)
        {
            float t = n > 1 ? (float)i / (n - 1) * (a.Length - 1) : 0f;
            int j = Mathf.Clamp(Mathf.FloorToInt(t), 0, a.Length - 2 < 0 ? 0 : a.Length - 2);
            float f = t - j;
            o[i] = (j + 1 < a.Length) ? Mathf.Lerp(a[j], a[j + 1], f) : a[a.Length - 1];
        }
        return o;
    }

    // Chaikin đồng thời cho polyline và dãy bán kính (giữ số điểm khớp nhau)
    private void ChaikinBoth(List<Vector3> pts, List<float> rad, int iters, out Vector3[] op, out float[] orad)
    {
        var cp = new List<Vector3>(pts);
        var cr = new List<float>(rad);
        for (int it = 0; it < iters; it++)
        {
            if (cp.Count < 3) break;
            var np = new List<Vector3>(cp.Count * 2);
            var nr = new List<float>(cp.Count * 2);
            np.Add(cp[0]); nr.Add(cr[0]);
            for (int i = 0; i < cp.Count - 1; i++)
            {
                np.Add(cp[i] * 0.75f + cp[i + 1] * 0.25f); nr.Add(cr[i] * 0.75f + cr[i + 1] * 0.25f);
                np.Add(cp[i] * 0.25f + cp[i + 1] * 0.75f); nr.Add(cr[i] * 0.25f + cr[i + 1] * 0.75f);
            }
            np.Add(cp[cp.Count - 1]); nr.Add(cr[cr.Count - 1]);
            cp = np; cr = nr;
        }
        op = cp.ToArray(); orad = cr.ToArray();
    }

    // Bán kính trung bình của bó tại mức L
    private float GroupRadius(List<int> grp, int L, List<float[]> tipRadArr, int[] tipMaxLevel)
    {
        float s = 0f; int c = 0;
        foreach (int ti in grp) { int l = Mathf.Min(L, tipMaxLevel[ti]); s += tipRadArr[ti][l]; c++; }
        return c > 0 ? s / c : 0f;
    }

    // Trung bình vị trí các tip trong bó ở mức L (tip nào chưa tới mức đó thì lấy mức sâu nhất của nó)
    // Xác nhận tách: hai nhóm đang PHÂN KỲ (xa nhau dần) -> tách thật; nếu xích lại -> jitter
    private bool ConfirmSplit(List<List<int>> groups, int nowL, int cl, List<Vector3[]> tipPosArr, int[] tipMaxLevel, float splitDist)
    {
        float dNow = MinGroupGap(groups, nowL, tipPosArr, tipMaxLevel);
        float dLater = MinGroupGap(groups, cl, tipPosArr, tipMaxLevel);
        // tách thật nếu ở mức sâu hơn khoảng cách nhóm KHÔNG nhỏ đi (đang phân kỳ hoặc giữ nguyên rộng)
        return dLater >= dNow && dLater > splitDist * 0.5f;
    }

    // khoảng cách nhỏ nhất giữa tâm các nhóm ở mức L
    private float MinGroupGap(List<List<int>> groups, int L, List<Vector3[]> tipPosArr, int[] tipMaxLevel)
    {
        var gpos = new List<Vector3>();
        foreach (var g in groups)
        {
            Vector3 c = Vector3.zero; int cnt = 0;
            foreach (int ti in g) { int l = Mathf.Min(L, tipMaxLevel[ti]); c += tipPosArr[ti][l]; cnt++; }
            gpos.Add(cnt > 0 ? c / cnt : c);
        }
        float best = float.MaxValue;
        for (int a = 0; a < gpos.Count; a++)
            for (int b = a + 1; b < gpos.Count; b++)
                best = Mathf.Min(best, Vector3.Distance(gpos[a], gpos[b]));
        return best;
    }

    private Vector3 GroupMean(List<int> grp, int L, List<Vector3[]> tipPosArr, int[] tipMaxLevel)
    {
        Vector3 c = Vector3.zero; int cnt = 0;
        foreach (int ti in grp)
        {
            int l = Mathf.Min(L, tipMaxLevel[ti]);
            c += tipPosArr[ti][l]; cnt++;
        }
        return cnt > 0 ? c / cnt : c;
    }

    // Tìm mức "fork" - nơi các nhóm con vừa rời nhau vượt ~nửa bán kính ống (chỗ tách thật).
    private int FindForkLevel(List<List<int>> groups, int lo, int hi, List<Vector3[]> tipPosArr, List<float[]> tipRadArr, int[] tipMaxLevel, float forkGap)
    {
        for (int l = lo; l <= hi; l++)
        {
            float dmax = 0f;
            for (int a = 0; a < groups.Count; a++)
                for (int b = a + 1; b < groups.Count; b++)
                {
                    float d = Vector3.Distance(GroupMean(groups[a], l, tipPosArr, tipMaxLevel),
                                               GroupMean(groups[b], l, tipPosArr, tipMaxLevel));
                    if (d > dmax) dmax = d;
                }
            float r = 0f; int c = 0;
            foreach (var g in groups) { r += GroupRadius(g, l, tipRadArr, tipMaxLevel); c++; }
            r = c > 0 ? r / c : 0f;
            if (dmax > r * forkGap) return Mathf.Max(lo, l - 1);   // vừa vượt -> fork ngay trước đó
        }
        return hi;   // chưa tách rõ tới tận L -> đặt tại L (xa nhất về phía đầu mút)
    }

    // Chia bó tại mức L thành các nhóm: 2 tip cùng nhóm nếu vị trí (ở mức L) gần nhau < splitDist
    private List<List<int>> SplitGroup(List<int> grp, int L, List<Vector3[]> tipPosArr, int[] tipMaxLevel, float splitDist)
    {
        int m = grp.Count;
        int[] uf = new int[m];
        for (int i = 0; i < m; i++) uf[i] = i;

        Vector3[] pos = new Vector3[m];
        for (int i = 0; i < m; i++)
        {
            int l = Mathf.Min(L, tipMaxLevel[grp[i]]);
            pos[i] = tipPosArr[grp[i]][l];
        }

        for (int a = 0; a < m; a++)
            for (int b = a + 1; b < m; b++)
                if (Vector3.Distance(pos[a], pos[b]) < splitDist)
                    Union(uf, a, b);

        var map = new Dictionary<int, List<int>>();
        for (int i = 0; i < m; i++)
        {
            int r = Find(uf, i);
            if (!map.TryGetValue(r, out var lst)) { lst = new List<int>(); map[r] = lst; }
            lst.Add(grp[i]);
        }
        return new List<List<int>>(map.Values);
    }

    private int Find(int[] uf, int x)
    {
        while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; }
        return x;
    }

    private void Union(int[] uf, int a, int b)
    {
        int ra = Find(uf, a), rb = Find(uf, b);
        if (ra != rb) uf[ra] = rb;
    }

    private float[] Dijkstra(Vector3[] verts, HashSet<int>[] adj, int source, out int[] prev)
    {
        int n = verts.Length;
        float[] dist = new float[n];
        prev = new int[n];
        for (int i = 0; i < n; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
        dist[source] = 0f;

        // Priority queue: SortedSet với key (dist, idx) đảm bảo duy nhất
        var pq = new SortedSet<(float d, int idx)>();
        pq.Add((0f, source));

        while (pq.Count > 0)
        {
            var top = pq.Min;
            pq.Remove(top);
            int u = top.idx;
            if (top.d > dist[u]) continue;

            foreach (int v in adj[u])
            {
                float nd = dist[u] + Vector3.Distance(verts[u], verts[v]);
                if (nd < dist[v])
                {
                    if (dist[v] < float.MaxValue)
                        pq.Remove((dist[v], v));
                    dist[v] = nd;
                    prev[v] = u;   // nhớ đỉnh liền trước để truy vết đường
                    pq.Add((nd, v));
                }
            }
        }
        return dist;
    }

    private void AddEdge(int fromNodeId, int toNodeId)
    {
        var from = network.nodes[fromNodeId];
        var to = network.nodes[toNodeId];

        var edge = new AirwayEdge
        {
            id = network.edges.Count,
            fromNodeId = fromNodeId,
            toNodeId = toNodeId,
            pathPoints = new List<Vector3> { from.position, to.position },
            length = Vector3.Distance(from.position, to.position),
            diameter = from.radius * 2f
        };

        network.edges.Add(edge);
        from.childEdges.Add(edge.id);
        to.parentEdge = edge.id;
    }

    private void CalculateEdgeParameters()
    {
        foreach (var edge in network.edges)
        {
            var fromNode = network.nodes[edge.fromNodeId];

            // Bảo toàn khối lượng đơn giản: chia đều theo số nhánh con
            edge.branchingRatio = fromNode.childEdges.Count > 0
                ? 1f / fromNode.childEdges.Count : 1f;

            // Hagen-Poiseuille xấp xỉ: v ~ 1/d^2 so với inlet
            var root = network.nodes[network.rootNodeId];
            float rootDiameter = Mathf.Max(root.radius * 2f, 0.001f);
            float ratio = rootDiameter / Mathf.Max(edge.diameter, 0.001f);
            edge.velocity = baseVelocity * ratio * ratio * edge.branchingRatio;
            edge.density = baseDensity;
        }
    }

    // ===== Getters =====

    public AirwayNode GetNode(int id) => (id >= 0 && id < network.nodes.Count) ? network.nodes[id] : null;
    public AirwayEdge GetEdge(int id) => (id >= 0 && id < network.edges.Count) ? network.edges[id] : null;

    // ===== Visualization =====

    private void OnDrawGizmos()
    {
        // Edit mode: tự build 1 lần để hiện ngay trong Scene view (không cần Play)
        if (!isBuilt)
        {
            var mf = GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                BuildNetwork();
        }

        if (!isBuilt || network == null) return;

        // PATH GỐC ĐÃ CENTER - XANH LÁ (green), mỗi path là polyline
        if (showGreenPaths)
        {
            Gizmos.color = Color.green;
            foreach (var e in treeEdgesRaw)
                for (int i = 0; i < e.Length - 1; i++)
                    Gizmos.DrawLine(e[i], e[i + 1]);
        }

        // CÂY MỚI đã center + hợp nhất - XANH DƯƠNG (blue), mỗi cạnh là polyline
        if (showBluePaths)
        {
            Gizmos.color = Color.blue;
            foreach (var e in treeEdges)
                for (int i = 0; i < e.Length - 1; i++)
                    Gizmos.DrawLine(e[i], e[i + 1]);
        }

        // NGÃ RẼ - TÍM (to gấp đôi) - ẩn cùng blue path
        if (showBluePaths)
        {
            Gizmos.color = new Color(0.6f, 0.1f, 0.9f);
            foreach (var bp in branchPoints)
                Gizmos.DrawSphere(bp, nodeSize);
        }

        // ĐẦU CUỐNG (cam) + PHẾ NANG (vàng)
        foreach (var node in network.nodes)
        {
            if (node.isRoot)
            {
                Gizmos.color = new Color(1f, 0.5f, 0f);
                Gizmos.DrawSphere(node.position, nodeSize * 2.5f);
            }
            else if (node.isLeaf && showAlveoli)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(node.position, nodeSize);
            }
        }
    }

    // Rebuild lại khi chỉnh tham số trong Inspector (Edit mode)
    private void OnValidate()
    {
        // Chỉ tự dựng lại khi chỉnh Inspector nếu bật rebuildOnEdit -> tắt để nhẹ khi chạy.
        // Đổi tham số có thể đổi topology -> xóa cache dải để dựng lại từ đầu.
        if (rebuildOnEdit) { isBuilt = false; ClearBandCache(); }
    }

    [ContextMenu("Rebuild Topology (xoa cache dai)")]
    public void RebuildTopology() { ClearBandCache(); BuildNetwork(); }

    private bool _rebuilding = false;
    private void Update()
    {
        // Trong khi Play: dựng lại blue path định kỳ theo mesh biến dạng (hô hấp) - CHẠY NỀN chia khung.
        if (!Application.isPlaying || !rebuildDuringPlay) return;
        _rebuildTimer += Time.deltaTime;
        if (_rebuildTimer < rebuildInterval || _rebuilding) return;   // chưa tới hạn hoặc đang dựng -> bỏ qua
        _rebuildTimer = 0f;
        StartCoroutine(RebuildBackground());
    }

    private System.Collections.IEnumerator RebuildBackground()
    {
        _rebuilding = true;
        var sw = logRebuildTime ? System.Diagnostics.Stopwatch.StartNew() : null;
        var e = BuildRoutine(true);        // chia khung theo buildBudgetMs
        while (e.MoveNext()) yield return e.Current;
        if (sw != null) { sw.Stop(); Debug.Log($"[AirwayPathBuilder] Rebuild nen xong sau {sw.Elapsed.TotalMilliseconds:F0} ms tong (chia nhieu khung)"); }
        _rebuilding = false;
    }
}