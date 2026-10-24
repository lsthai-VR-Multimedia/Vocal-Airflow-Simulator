using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public class VectorFieldBaker : MonoBehaviour
{
    private float arrowLength = 0.35f;
    private int gizmoStep = 2;

    [Header("Bake — đo tiết diện ống")]
    [Range(1, 50)]
    [Tooltip("Nhân tải CPU trực tiếp — chạy lặp lại đúng bước đo tiết diện (Parallel.For) N lần cho mỗi path (chỉ giữ kết quả lần cuối). Tăng số này để ép FPS giảm theo đúng tỉ lệ mong muốn, không phụ thuộc rays/số path/số điểm. 1 = không nhân tải (bình thường).")]
    [SerializeField] private int computeLoadMultiplier = 1;
    [Range(4, 128)]
    [Tooltip("Số tia ray-march mỗi điểm path khi đo tiết diện (MeasureCrossSection) — tăng số này để tăng tải tính toán thật (CPU) trực tiếp, không cần sửa code. Mỗi điểm path tốn rays×2 lần ray-march (1 lượt tái tâm + 1 lượt đo lại). Mặc định gốc là 16; tăng lên 32/64 nếu cần tải nặng hơn để test.")]
    [SerializeField] private int crossSectionRays = 16;

    [Range(0f, 1f)]
    [Tooltip("Phân vị dùng khi suy bán kính từ các tia đo (0 = cực tiểu tuyệt đối, dễ tràn/nén; 1 = cực đại, dễ tràn ra ngoài). 0.2-0.3 là điểm cân bằng tốt cho lưới voxel thô.")]
    [SerializeField] private float radiusPercentile = 0.25f;

    [Range(0f, 1f)]
    [Tooltip("Hệ số co bán kính vào gần tâm lòng ống để an toàn (1 = giữ nguyên bán kính đo được, càng nhỏ càng co vào tâm nhiều). Áp dụng lên mọi path, giảm ngay hiện tượng hạt tràn ra ngoài rải rác mà không cần dò lại ngưỡng voxel.")]
    [SerializeField] private float radiusSafetyShrink = 0.6f;

    [Header("Rebake nền — gối nhau vô hạn khi animation hô hấp thay đổi mesh")]
    [SerializeField] private bool enablePeriodicRebake = false;
    [SerializeField] private float rebakeInterval = 0.5f;
    [SerializeField] private int maxConcurrentRebakes = 10;

    [Tooltip("BẬT: rebake chạy tuần tự thật (đợi lượt trước xong mới tính lượt sau) — bắt buộc để đo đúng vấn đề 'FPS/thời gian theo độ phân giải voxel' và 'có bám kịp rebakeInterval hay không'. TẮT: quay lại kiểu cũ, bắn lượt mới mỗi rebakeInterval bất kể lượt trước xong chưa, tối đa maxConcurrentRebakes lượt chồng nhau (dùng khi cần stress-test tranh chấp CPU).")]
    [SerializeField] private bool strictSequentialRebake = true;

    [Tooltip("Số lần 1 lượt rebake mất LÂU HƠN rebakeInterval (chỉ đếm khi strictSequentialRebake bật) — phản ánh trực tiếp việc có bám kịp thời gian thực hay không.")]
    [SerializeField] private int missedDeadlineCount;

    [Tooltip("Ngưỡng chờ tối đa (giây) cho 1 lượt rebake trước khi coi như đã treo và bỏ qua, tiếp tục lượt mới — đảm bảo RebakeLoopForever không bao giờ đứng yên vĩnh viễn dù có lỗi/treo bất kỳ bên trong.")]
    [SerializeField] private float watchdogTimeoutSec = 30f;

    [Tooltip("Số lần watchdog phải can thiệp vì 1 lượt rebake vượt quá watchdogTimeoutSec — > 0 nghĩa là có lượt bị treo thật.")]
    [SerializeField] private int watchdogTimeoutCount;

    [Header("Batch theo độ nặng công việc (item = 1 voxel hoặc 1 bước xử lý)")]
    [Tooltip("Ngân sách thời gian mỗi khối xử lý trước khi nhường 1 khung hình (ms) — giống hệt cơ chế buildBudgetMs của thuật toán Confluence, dùng Stopwatch thời gian thực thay vì đếm số phần tử, để 2 thuật toán so sánh công bằng trên cùng một cách tiếp cận.")]
    [SerializeField] private float buildBudgetMs = 40f;

    [Header("Đo hiệu năng (chỉ đọc)")]
    [SerializeField] private float lastRebakeDurationMs;
    [SerializeField] private int rebakeFrameCount;
    [SerializeField] private int totalRebakeCount;
    [SerializeField] private int concurrentRebakesRunning;

    private Coroutine _rebakeLoopCo;
    private bool _rebakeLoopAlive;
    private float _diagLogTimer;
    private int _flowYieldCount;

    // Stopwatch dùng chung cho toàn bộ 1 lượt rebake — reset lúc bắt đầu RebakeOneGeneration.
    // Cùng cơ chế budget thời gian thực (buildBudgetMs) như thuật toán Confluence, thay cho
    // đếm số phần tử (workItemsPerYieldLight/Heavy cũ) — đảm bảo 2 thuật toán so sánh công
    // bằng trên cùng một cách chia nhỏ công việc.
    private System.Diagnostics.Stopwatch _bsw;

    // Kiểm tra đã dùng hết ngân sách thời gian (buildBudgetMs) cho khối hiện tại chưa — nếu
    // rồi thì reset lại đồng hồ để tính khối tiếp theo. Gọi hàm này (không phải kiểm tra trực
    // tiếp mỗi vòng lặp) để giảm chi phí đọc Stopwatch liên tục.
    bool BudgetElapsed()
    {
        if (_bsw.ElapsedMilliseconds >= buildBudgetMs)
        {
            _bsw.Restart();
            return true;
        }
        return false;
    }

    // Đếm CỘNG DỒN số lượt đã đi qua từng giai đoạn (không reset) — để biết chính xác 48
    // lượt đang thực sự kẹt ở giai đoạn nào, thay vì chỉ nhìn concurrentRebakesRunning (chỉ
    // biết đang chạy, không biết chạy TỚI ĐÂU).
    private int _stageRevoxelizeDone, _stageDistFieldDone, _stageWallDistDone, _stageAlveoliDone, _statePathsDone, _stageFlowFieldDone;

    // Một điểm dọc theo đường trung tâm nhánh, kèm frame song song hóa (parallel-transport,
    // double-reflection method) và bán kính an toàn đo TRỰC TIẾP từ hình học voxel bằng
    // ray-march — thay cho cách suy bán kính từ 1 hằng số hiệu chỉnh toàn cục (nguyên nhân
    // khiến hạt tràn ra ngoài mặt ống), và frame liên tục dọc path — thay cho việc tính lại
    // Cross(T, up) độc lập tại mỗi điểm (nguyên nhân khiến hạt "lật" hướng đột ngột).
    public class PathPoint
    {
        public Vector3 pos;
        public Vector3 t;      // tangent
        public Vector3 u;      // frame axis 1
        public Vector3 v;      // frame axis 2
        public float radius;   // bán kính an toàn tại điểm này (world units)
        public float cum;      // chiều dài cung tính từ điểm đầu path
    }

    public class PathInfo
    {
        public List<PathPoint> pts = new List<PathPoint>();
        public float length;
    }

    private class FieldSet
    {
        public int rx, ry, rz;
        public Bounds bounds;
        public Vector3 voxelSize;
        public bool[,,] grid;
        public int[,,] distField;
        public int[,,] wallDist;
        public int maxWallDist = 1;
        public List<Vector3Int> alveoli = new List<Vector3Int>();
        public List<PathInfo> paths = new List<PathInfo>();
        public Vector3[,,] flowField;
        public List<Vector3Int> inletCells = new List<Vector3Int>();   // cache voxel cửa vào
    }

    private FieldSet _current;
    private bool _baked = false;

    private Voxelizer _voxelizer;
    private GameObject _fieldMeshGO;
    private Material _arrowMat;
    private Material _headMat;
    private Mesh _coneMesh;

    private static readonly int[] DX26 = { 1, -1, 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 0, 0, 0, 0, 1, 1, 1, 1, -1, -1, -1, -1 };
    private static readonly int[] DY26 = { 0, 0, 1, -1, 0, 0, 1, -1, 1, -1, 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 1, 1, -1, -1 };
    private static readonly int[] DZ26 = { 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1 };

    public bool IsBaked => _baked;
    public int RebakeGeneration => totalRebakeCount;

    // Kiểm tra 1 vị trí thế giới có nằm TRONG lòng ống theo lưới voxel hiện tại (_current)
    // hay không — dùng để đo "containment rate" (tỉ lệ hạt particle thực sự nằm trong đường
    // thở), một chỉ số đánh giá TÍNH ĐÚNG của mô phỏng, độc lập với các chỉ số hiệu năng
    // (FPS/thời gian rebake) đã đo trước đó. Đọc trực tiếp fs.grid của thế hệ mới nhất — an
    // toàn để gọi bất kỳ lúc nào (kể cả giữa lúc đang rebake nền, vì _current chỉ được gán
    // MỘT LẦN duy nhất mỗi khi 1 thế hệ hoàn tất, không bị sửa dở dang).
    public bool IsWorldPosInsideAirway(Vector3 worldPos)
    {
        var fs = _current;
        if (fs == null || fs.grid == null) return false;
        int xi = Mathf.FloorToInt((worldPos.x - fs.bounds.min.x) / fs.voxelSize.x);
        int yi = Mathf.FloorToInt((worldPos.y - fs.bounds.min.y) / fs.voxelSize.y);
        int zi = Mathf.FloorToInt((worldPos.z - fs.bounds.min.z) / fs.voxelSize.z);
        if (xi < 0 || xi >= fs.rx || yi < 0 || yi >= fs.ry || zi < 0 || zi >= fs.rz) return false;
        return fs.grid[xi, yi, zi];
    }
    public float VoxelStepY => _current != null ? _current.voxelSize.y : 0f;
    public int PathCount => _current != null ? _current.paths.Count : 0;

    // ==================== MẢNG LỚN — CẤP RIÊNG CHO TỪNG FieldSet ====================
    //
    // Trước đây dùng chung 1 pool (Stack<T>) cho mọi lượt rebake để giảm áp lực GC, dựa
    // trên giả định rebake chạy TUẦN TỰ (mỗi lúc chỉ 1 FieldSet "cũ" bị thay thế, an toàn
    // để trả mảng của nó về pool). Nhưng khi nhiều lượt rebake chạy SONG SONG (đúng thiết
    // kế hiện tại — kích hoạt cố định mỗi rebakeInterval bất kể lượt trước xong hay chưa),
    // các lượt hoàn tất xen kẽ không theo thứ tự có thể khiến một FieldSet VỪA trở thành
    // _current (đang được dùng) lại bị một lượt khác hoàn tất sau đó nhầm là "thế hệ cũ" và
    // trả mảng của nó về pool — mảng đó sau đó bị một lượt thứ 3 rút ra và ghi đè trong khi
    // vẫn đang là _current, làm hỏng ngầm dữ liệu đang hiển thị. Lỗi này tích lũy dần qua
    // các lượt rebake (dữ liệu hỏng lan sang các bước sau như flood-fill tìm phế nang),
    // khiến khối lượng tính toán mỗi lượt tăng dần theo thời gian — đúng nguyên nhân FPS
    // giảm dần không hồi phục dù số hạt particle đã giảm lại (theo benchmark bạn đo được).
    //
    // Bỏ pool dùng chung, để mỗi FieldSet luôn cấp mảng RIÊNG của chính nó — không còn khả
    // năng 2 thế hệ rebake cùng sở hữu 1 mảng, loại bỏ hẳn nguồn gốc lỗi trên. Đổi lại tăng
    // áp lực GC (không tái sử dụng mảng lớn nữa) — chấp nhận được vì ưu tiên đúng dữ liệu.

    int[,,] RentIntArrayRaw(int rx, int ry, int rz)
    {
        return new int[rx, ry, rz];
    }

    void ReturnIntArray(int[,,] arr)
    {
        // không còn pool dùng chung — để GC thu dọn bình thường
    }

    Vector3[,,] RentVec3Array(int rx, int ry, int rz)
    {
        return new Vector3[rx, ry, rz];
    }

    void ReturnVec3Array(Vector3[,,] arr)
    {
        // không còn pool dùng chung — để GC thu dọn bình thường
    }
    public PathInfo GetPathAt(int index)
    {
        var fs = _current;
        if (fs == null || index < 0 || index >= fs.paths.Count) return null;
        return fs.paths[index];
    }
    void Awake()
    {
        _voxelizer = GetComponent<Voxelizer>();
        if (_voxelizer == null)
        {
            Debug.LogError("VectorFieldBaker: không tìm thấy Voxelizer");
            return;
        }
        WarmUpJit();
        StartCoroutine(WaitForVoxelizer());
    }

    // Chạy các hàm hot-path (MeasureCrossSection...) trên dữ liệu GIẢ, cực nhỏ, TRƯỚC khi lượt
    // bake thật đầu tiên bắt đầu — để .NET/Mono JIT kịp biên dịch lại bản tối ưu (tiered
    // compilation) NGAY TỪ ĐẦU, thay vì để việc "làm nóng" này diễn ra âm thầm trong lúc chạy
    // lượt bake thật (biểu hiện ra ngoài là hiệu năng tăng dần một cách khó hiểu trong ~20-30s
    // đầu, dễ bị hiểu nhầm là lỗi thuật toán). Đây là warm-up ở TẦNG ỨNG DỤNG — chạy 1 lần khi
    // khởi động, có lợi cho MỌI người dùng, không chỉ lúc benchmark.
    void WarmUpJit()
    {
        var fs = new FieldSet();
        fs.rx = fs.ry = fs.rz = 6;
        fs.bounds = new Bounds(Vector3.zero, Vector3.one * 6f);
        fs.voxelSize = Vector3.one;
        fs.grid = new bool[fs.rx, fs.ry, fs.rz];
        for (int x = 1; x < fs.rx - 1; x++)
            for (int y = 1; y < fs.ry - 1; y++)
                for (int z = 1; z < fs.rz - 1; z++)
                    fs.grid[x, y, z] = true; // 1 khối đặc nhỏ ở giữa, đủ để các tia ray-march có "thành" để chạm tới

        Vector3 center = new Vector3(3, 3, 3);
        Vector3 u = Vector3.right, v = Vector3.forward;
        float maxSearch = 6f;

        // Gọi lặp lại đủ nhiều lần để vượt ngưỡng "hot method" điển hình của JIT tiered
        // compilation (thường dưới 100 lượt gọi) — con số 200 dư dả để chắc chắn kích hoạt.
        for (int i = 0; i < 200; i++)
            MeasureCrossSection(fs, center, u, v, maxSearch);
    }

    void Update()
    {
        if (!_baked) return;

        if (Input.GetKeyDown(KeyCode.R)) ShowVectorField();
        if (Input.GetKeyDown(KeyCode.T)) HideVectorField();

        // Log chẩn đoán, chạy 1 lần/giây, KHÔNG đổi hành vi gì — chỉ để xác nhận
        // concurrentRebakesRunning có bị kẹt ở mức trần (maxConcurrentRebakes) hay không
        // trong lúc FPS đang tụt dần, tức là chưa lượt rebake nào kịp hoàn tất để log dòng
        // "[Rebake #...]" ở cuối RebakeOneGeneration.
        _diagLogTimer += Time.deltaTime;
        if (_diagLogTimer >= 1f)
        {
            _diagLogTimer = 0f;
            string bamKip = totalRebakeCount > 0
                ? (lastRebakeDurationMs <= rebakeInterval * 1000f ? "BÁM KỊP" : "TRỄ")
                : "chưa có dữ liệu";
            Debug.Log($"[Diag] t={Time.realtimeSinceStartup:F1}s | song song: {concurrentRebakesRunning}/{maxConcurrentRebakes} | hoàn tất: {totalRebakeCount} | " +
                      $"lượt gần nhất: {lastRebakeDurationMs:F0}ms (mốc cần đạt: {rebakeInterval * 1000f:F0}ms) → {bamKip} | trễ hạn: {missedDeadlineCount} lần | " +
                      $"lũy kế qua từng giai đoạn — voxelize:{_stageRevoxelizeDone} distField:{_stageDistFieldDone} " +
                      $"wallDist:{_stageWallDistDone} alveoli:{_stageAlveoliDone} paths:{_statePathsDone} flow:{_stageFlowFieldDone}");
        }

        // Tự khởi động lại nếu vòng lặp rebake đã CHẾT giữa chừng (do lỗi chưa lường trước) —
        // trước đây chỉ kiểm tra _rebakeLoopCo == null, nhưng biến này KHÔNG tự về null khi
        // coroutine bên trong chết vì exception, nên vòng lặp chết là đứng im vĩnh viễn, không
        // bao giờ tự chạy lại (đúng nguyên nhân của hiện tượng "FPS tăng vọt và đứng yên").
        // _rebakeLoopAlive do chính RebakeLoopForever tự cập nhật (true lúc bắt đầu, false ở
        // finally lúc kết thúc dù bình thường hay do lỗi).
        if (enablePeriodicRebake && !_rebakeLoopAlive)
        {
            if (_rebakeLoopCo != null) StopCoroutine(_rebakeLoopCo);
            _rebakeLoopCo = StartCoroutine(RebakeLoopForever());
        }
        else if (!enablePeriodicRebake && _rebakeLoopCo != null)
        {
            StopCoroutine(_rebakeLoopCo);
            _rebakeLoopCo = null;
            _rebakeLoopAlive = false;
        }
    }

    IEnumerator WaitForVoxelizer()
    {
        while (!_voxelizer.IsDone)
            yield return null;

        var fs = new FieldSet();
        fs.bounds = _voxelizer.Bounds;
        fs.voxelSize = _voxelizer.VoxelSize;
        fs.grid = _voxelizer.VoxelGrid;

        if (fs.grid == null)
        {
            Debug.LogError("VectorFieldBaker: VoxelGrid null!");
            yield break;
        }

        fs.rx = fs.grid.GetLength(0);
        fs.ry = fs.grid.GetLength(1);
        fs.rz = fs.grid.GetLength(2);

        Debug.Log("Building distance field...");
        BuildDistanceField(fs);
        Debug.Log("Building wall distance...");
        BuildWallDistance(fs);
        Debug.Log("Finding alveoli & paths...");
        FindAlveoli(fs);
        BuildPaths(fs);
        Debug.Log("Building flow field...");
        BuildFlowField(fs);

        _current = fs;
        _baked = true;
        Debug.Log("Bake done. Nhấn R hiện, T ẩn.");
    }

    // ==================== VÒNG LẶP NỀN — GIỚI HẠN SỐ LƯỢT SONG SONG ====================

    IEnumerator RebakeLoopForever()
    {
        Debug.Log("[VectorFieldBaker] RebakeLoopForever BẮT ĐẦU");
        _rebakeLoopAlive = true;
        int loopIteration = 0;
        try
        {
            while (enablePeriodicRebake)
            {
                loopIteration++;

                if (strictSequentialRebake)
                {
                    // KHÔNG chờ rebakeInterval trước khi bắt đầu lượt mới nữa — trước đây dù
                    // 1 lượt mất tới 16s (vượt xa rebakeInterval=0.5s), vòng lặp vẫn chèn thêm
                    // 0.5s "nghỉ giả" sau mỗi lượt trước khi bắt đầu lượt tiếp theo. Trong lúc
                    // nghỉ đó CPU hoàn toàn rảnh → FPS vọt cao bất thường, tạo cảm giác sai
                    // lệch rằng hệ thống "khỏe" trong khi thực chất phần lớn thời gian không hề
                    // cập nhật gì. Bỏ khoảng nghỉ này: lượt tiếp theo bắt đầu NGAY khi lượt
                    // trước vừa xong — máy tính toán liên tục không ngắt quãng, để đồ thị FPS
                    // phản ánh đúng tải thật xuyên suốt, không còn đỉnh cao gây hiểu lầm.
                    //
                    // Đợi lượt trước XONG HẲN rồi mới bắt đầu lượt sau — điều kiện BẮT BUỘC để
                    // đo được: (1) FPS/thời gian 1 lượt phản ánh đúng độ nặng của thuật toán
                    // theo độ phân giải voxel, không bị nhiễu bởi nhiều lượt tranh chấp CPU
                    // cùng lúc; (2) so sánh trực tiếp thời gian 1 lượt với mốc rebakeInterval để
                    // biết có bám kịp yêu cầu thời gian thực hay không.
                    if (concurrentRebakesRunning < maxConcurrentRebakes)
                    {
                        // KHÔNG dùng "yield return StartCoroutine(...)" trực tiếp nữa — nếu
                        // coroutine con chết vì exception, Unity không đảm bảo báo hoàn tất
                        // đúng cách ra ngoài, khiến dòng yield này TREO VĨNH VIỄN (không lỗi,
                        // không log, chỉ đứng im mãi — đúng hiện tượng "FPS tăng vọt rồi đứng
                        // yên, không có KẾT THÚC nào" quan sát được). Thay bằng tín hiệu "xong"
                        // độc lập (doneFlag) do chính RebakeOneGeneration tự set trong finally
                        // của NÓ, kèm ngưỡng chờ tối đa (watchdogTimeoutSec) — quá hạn thì coi
                        // như lượt đó đã treo, bỏ qua và tiếp tục lượt mới, không bao giờ đứng
                        // yên vĩnh viễn nữa.
                        bool[] doneFlag = { false };
                        StartCoroutine(RebakeOneGeneration(doneFlag));

                        float startTime = Time.realtimeSinceStartup;
                        while (!doneFlag[0] && (Time.realtimeSinceStartup - startTime) < watchdogTimeoutSec)
                            yield return null;

                        float elapsed = Time.realtimeSinceStartup - startTime;
                        if (!doneFlag[0])
                        {
                            Debug.LogError($"[VectorFieldBaker] Lượt rebake vượt quá {watchdogTimeoutSec}s KHÔNG báo hoàn tất — " +
                                            $"coi như đã treo, bỏ qua và tiếp tục lượt mới. concurrentRebakesRunning={concurrentRebakesRunning}");
                            watchdogTimeoutCount++;
                        }
                        if (elapsed > rebakeInterval)
                            missedDeadlineCount++;
                    }
                }
                else
                {
                    // Chế độ cũ — bắn lượt mới bất kể lượt trước xong hay chưa, tối đa
                    // maxConcurrentRebakes lượt chồng nhau. Vẫn cần chờ đúng lịch cố định
                    // rebakeInterval ở đây (khác nhánh tuần tự phía trên) vì đây chính là ý
                    // nghĩa của chế độ này — kích hoạt đều đặn theo giờ đồng hồ để stress-test,
                    // không phải chạy liên tục không ngừng như nhánh tuần tự.
                    float waited = 0f;
                    while (waited < rebakeInterval)
                    {
                        waited += Time.deltaTime;
                        yield return null;
                    }

                    if (concurrentRebakesRunning < maxConcurrentRebakes)
                    {
                        StartCoroutine(RebakeOneGeneration(new bool[1]));
                    }
                }
            }
        }
        finally
        {
            _rebakeLoopAlive = false;
            // Chạy dù thoát bình thường (enablePeriodicRebake=false) hay do exception —
            // nếu bạn thấy dòng này xuất hiện SỚM (trong lúc enablePeriodicRebake vẫn đang
            // true), nghĩa là vòng lặp rebake đã CHẾT do lỗi, giải thích trực tiếp hiện tượng
            // "FPS tăng vọt và đứng yên" — vì Update() không tự khởi động lại coroutine đã
            // chết (chỉ khởi động khi _rebakeLoopCo == null, mà biến này vẫn giữ tham chiếu
            // cũ dù coroutine bên trong đã chết).
            Debug.LogWarning($"[VectorFieldBaker] RebakeLoopForever KẾT THÚC sau {loopIteration} vòng lặp | " +
                              $"enablePeriodicRebake={enablePeriodicRebake} | t={Time.realtimeSinceStartup:F1}s");
        }
    }

    IEnumerator RebakeOneGeneration(bool[] doneFlag)
    {
        concurrentRebakesRunning++;

        // Bọc toàn bộ bằng try/finally — đảm bảo concurrentRebakesRunning LUÔN được giảm về
        // dù có lỗi bất kỳ xảy ra ở bước nào bên trong (kể cả các bước chưa lường trước).
        // Trước đây "concurrentRebakesRunning--" chỉ nằm ở các điểm return cụ thể; nếu 1
        // exception xảy ra ở đâu đó không được bắt, coroutine chết ngang mà không giảm biến
        // đếm — kẹt cứng ở mức trần, khiến rebake nền ngừng hẳn vĩnh viễn (không còn lượt mới
        // nào được lên lịch), biểu hiện ra ngoài là FPS đột ngột tăng vọt và đứng yên.
        // doneFlag[0] cũng được set true trong finally — tín hiệu "xong" ĐỘC LẬP với cơ chế
        // hoàn tất coroutine mặc định của Unity (không đáng tin cậy 100% khi có exception),
        // để RebakeLoopForever biết chắc chắn khi nào lượt này thực sự kết thúc.
        try
        {
            var totalSw = System.Diagnostics.Stopwatch.StartNew();
            _bsw = System.Diagnostics.Stopwatch.StartNew();
            int frameCounter = 1;

            bool[,,] grid = null;
            Bounds bounds = default;
            Vector3 voxelSize = default;
            bool gotResult = false;

            yield return StartCoroutine(_voxelizer.RevoxelizeCoroutine((g, b, vsz) =>
            {
                grid = g; bounds = b; voxelSize = vsz; gotResult = true;
            }));
            frameCounter++;
            _stageRevoxelizeDone++;

            if (!gotResult || grid == null)
                yield break;

            var fs = new FieldSet();
            fs.grid = grid;
            fs.bounds = bounds;
            fs.voxelSize = voxelSize;
            fs.rx = grid.GetLength(0);
            fs.ry = grid.GetLength(1);
            fs.rz = grid.GetLength(2);

            yield return StartCoroutine(CountFrames(BuildDistanceFieldCoroutine(fs), r => frameCounter += r));
            _stageDistFieldDone++;
            Debug.Log($"[Stage] distField xong t={Time.realtimeSinceStartup:F2}s");
            yield return StartCoroutine(CountFrames(BuildWallDistanceCoroutine(fs), r => frameCounter += r));
            _stageWallDistDone++;
            Debug.Log($"[Stage] wallDist xong t={Time.realtimeSinceStartup:F2}s");
            yield return StartCoroutine(CountFrames(FindAlveoliCoroutine(fs), r => frameCounter += r));
            _stageAlveoliDone++;
            Debug.Log($"[Stage] alveoli xong t={Time.realtimeSinceStartup:F2}s");
            yield return StartCoroutine(CountFrames(BuildPathsCoroutine(fs), r => frameCounter += r));
            _statePathsDone++;
            Debug.Log($"[Stage] paths XONG THẬT t={Time.realtimeSinceStartup:F2}s — chuẩn bị gọi BuildFlowFieldCoroutine ngay sau dòng này");
            yield return StartCoroutine(CountFrames(BuildFlowFieldCoroutine(fs), r => frameCounter += r));
            _stageFlowFieldDone++;

            FieldSet old = _current;
            _current = fs;

            if (old != null)
            {
                ReturnIntArray(old.distField);
                ReturnIntArray(old.wallDist);
                ReturnVec3Array(old.flowField);
            }

            totalSw.Stop();
            lastRebakeDurationMs = (float)totalSw.Elapsed.TotalMilliseconds;
            rebakeFrameCount = frameCounter;
            totalRebakeCount++;

            Debug.Log($"[Rebake #{totalRebakeCount}] {fs.alveoli.Count} phế nang, {fs.paths.Count} path | " +
                       $"{lastRebakeDurationMs:F1} ms qua {frameCounter} frame | " +
                       $"đang chạy song song: {concurrentRebakesRunning}");
        }
        finally
        {
            concurrentRebakesRunning--;
            doneFlag[0] = true;
        }
    }

    IEnumerator CountFrames(IEnumerator routine, System.Action<int> onDone)
    {
        int count = 0;
        while (routine.MoveNext())
        {
            count++;
            yield return routine.Current;
        }
        onDone(count);
    }

    // ==================== CÁC BƯỚC CON — batch theo độ nặng, dùng pool ====================

    IEnumerator BuildDistanceFieldCoroutine(FieldSet fs)
    {
        fs.distField = RentIntArrayRaw(fs.rx, fs.ry, fs.rz);

        // Reset mảng theo batch (nhẹ) — tránh khối công việc đồng bộ lớn dồn 1 frame
        int resetCounter = 0;
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    fs.distField[x, y, z] = -1;
                    resetCounter++;
                    if (resetCounter >= 1000)
                    {
                        resetCounter = 0;
                        if (BudgetElapsed())
                            yield return null;
                    }
                }

        Queue<Vector3Int> queue = new Queue<Vector3Int>();
        fs.inletCells.Clear();

        // Seed = lớp Y cao nhất có voxel — đồng thời chính là danh sách voxel cửa vào,
        // cache lại để GetRandomInletPoint không phải quét lại mỗi lần gọi.
        for (int y = fs.ry - 1; y >= 0 && queue.Count == 0; y--)
            for (int x = 0; x < fs.rx; x++)
                for (int z = 0; z < fs.rz; z++)
                    if (fs.grid[x, y, z])
                    {
                        fs.distField[x, y, z] = 0;
                        queue.Enqueue(new Vector3Int(x, y, z));
                        fs.inletCells.Add(new Vector3Int(x, y, z));
                    }

        int[] dx = { 1, -1, 0, 0, 0, 0 };
        int[] dy = { 0, 0, 1, -1, 0, 0 };
        int[] dz = { 0, 0, 0, 0, 1, -1 };

        int counter = 0;
        while (queue.Count > 0)
        {
            Vector3Int c = queue.Dequeue();
            int cd = fs.distField[c.x, c.y, c.z];

            for (int d = 0; d < 6; d++)
            {
                int nx = c.x + dx[d], ny = c.y + dy[d], nz = c.z + dz[d];
                if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                if (!fs.grid[nx, ny, nz]) continue;
                if (fs.distField[nx, ny, nz] != -1) continue;

                fs.distField[nx, ny, nz] = cd + 1;
                queue.Enqueue(new Vector3Int(nx, ny, nz));
            }

            counter++;
            if (counter >= 1000)
            {
                counter = 0;
                if (BudgetElapsed())
                    yield return null;
            }
        }
    }

    IEnumerator BuildWallDistanceCoroutine(FieldSet fs)
    {
        fs.wallDist = RentIntArrayRaw(fs.rx, fs.ry, fs.rz);

        int resetCounter = 0;
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    fs.wallDist[x, y, z] = -1;
                    resetCounter++;
                    if (resetCounter >= 1000)
                    {
                        resetCounter = 0;
                        if (BudgetElapsed())
                            yield return null;
                    }
                }

        Queue<Vector3Int> queue = new Queue<Vector3Int>();
        int[] dx6 = { 1, -1, 0, 0, 0, 0 };
        int[] dy6 = { 0, 0, 1, -1, 0, 0 };
        int[] dz6 = { 0, 0, 0, 0, 1, -1 };

        int seedCounter = 0;
        for (int x = 0; x < fs.rx; x++)
        {
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (!fs.grid[x, y, z]) continue;
                    bool nearWall = false;
                    for (int d = 0; d < 6; d++)
                    {
                        int nx = x + dx6[d], ny = y + dy6[d], nz = z + dz6[d];
                        if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz)
                        { nearWall = true; break; }
                        if (!fs.grid[nx, ny, nz]) { nearWall = true; break; }
                    }
                    if (nearWall)
                    {
                        fs.wallDist[x, y, z] = 0;
                        queue.Enqueue(new Vector3Int(x, y, z));
                    }

                    seedCounter++;
                    if (seedCounter >= 1000)
                    {
                        seedCounter = 0;
                        if (BudgetElapsed())
                            yield return null;
                    }
                }
        }

        int counter = 0;
        while (queue.Count > 0)
        {
            Vector3Int c = queue.Dequeue();
            int cd = fs.wallDist[c.x, c.y, c.z];
            for (int d = 0; d < 6; d++)
            {
                int nx = c.x + dx6[d], ny = c.y + dy6[d], nz = c.z + dz6[d];
                if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                if (!fs.grid[nx, ny, nz]) continue;
                if (fs.wallDist[nx, ny, nz] != -1) continue;
                fs.wallDist[nx, ny, nz] = cd + 1;
                queue.Enqueue(new Vector3Int(nx, ny, nz));
            }

            counter++;
            if (counter >= 1000)
            {
                counter = 0;
                if (BudgetElapsed())
                    yield return null;
            }
        }

        fs.maxWallDist = 1;
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                    if (fs.wallDist[x, y, z] > fs.maxWallDist)
                        fs.maxWallDist = fs.wallDist[x, y, z];
    }

    IEnumerator FindAlveoliCoroutine(FieldSet fs)
    {
        fs.alveoli = new List<Vector3Int>();
        int maxDist = 0;
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                    if (fs.distField[x, y, z] > maxDist) maxDist = fs.distField[x, y, z];

        int minDist = Mathf.RoundToInt(maxDist * 0.35f);
        int counter = 0;

        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (!fs.grid[x, y, z] || fs.distField[x, y, z] < 0) continue;
                    int cd = fs.distField[x, y, z];
                    if (cd < minDist) continue;

                    bool isMax = true;
                    for (int d = 0; d < 26 && isMax; d++)
                    {
                        int nx = x + DX26[d], ny = y + DY26[d], nz = z + DZ26[d];
                        if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                        if (fs.distField[nx, ny, nz] > cd) isMax = false;
                    }
                    if (isMax) fs.alveoli.Add(new Vector3Int(x, y, z));

                    counter++;
                    if (counter >= 1000)
                    {
                        counter = 0;
                        if (BudgetElapsed())
                            yield return null;
                    }
                }
    }

    IEnumerator BuildPathsCoroutine(FieldSet fs)
    {
        fs.paths = new List<PathInfo>();
        int counter = 0;

        foreach (var alv in fs.alveoli)
        {
            List<Vector3> path = new List<Vector3>();
            Vector3Int cur = alv;
            path.Add(GridToWorld(fs, cur.x, cur.y, cur.z));

            int guard = 0;
            while (fs.distField[cur.x, cur.y, cur.z] > 0 && guard < 10000)
            {
                guard++;
                int cd = fs.distField[cur.x, cur.y, cur.z];
                int bestDist = cd;
                Vector3Int best = cur;
                int bestWall = -1;

                for (int d = 0; d < 26; d++)
                {
                    int nx = cur.x + DX26[d], ny = cur.y + DY26[d], nz = cur.z + DZ26[d];
                    if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                    if (fs.distField[nx, ny, nz] < 0) continue;
                    if (fs.distField[nx, ny, nz] < cd)
                    {
                        if (fs.distField[nx, ny, nz] < bestDist ||
                            (fs.distField[nx, ny, nz] == bestDist && fs.wallDist[nx, ny, nz] > bestWall))
                        {
                            bestDist = fs.distField[nx, ny, nz];
                            bestWall = fs.wallDist[nx, ny, nz];
                            best = new Vector3Int(nx, ny, nz);
                        }
                    }
                }

                if (best == cur) break;
                cur = best;
                path.Add(GridToWorld(fs, cur.x, cur.y, cur.z));

                counter++;
                if (counter >= 1000)
                {
                    counter = 0;
                    if (BudgetElapsed())
                        yield return null;
                }
            }

            path.Reverse();
            if (path.Count > 1)
            {
                // Dùng bản coroutine — cho phép ngắt (nhường frame theo ngân sách buildBudgetMs)
                // NGAY TRONG lúc đo tiết diện từng điểm, không đợi xong hết cả phế nang mới
                // kiểm tra. Loại bỏ hiện tượng FPS tăng/giảm bất thường do JIT "nguội/nóng"
                // ảnh hưởng khác nhau lên độ dài mỗi khung hình khi 1 phế nang (đơn vị trước
                // đây) có thời lượng xử lý dao động mạnh.
                PathInfo info = null;
                yield return StartCoroutine(BuildPathInfoCoroutine(fs, path, r => info = r));
                if (info != null) fs.paths.Add(info);
            }
        }
    }

    // Làm mượt path thô (tâm voxel, hướng răng cưa do BFS 26-lân cận), dựng frame song
    // song hóa dọc path, và đo bán kính thật tại từng điểm — chạy 1 LẦN khi bake xong.
    // Particle sau đó chỉ nội suy trên dữ liệu này, không tính lại mỗi frame.
    // Phần dựng frame (làm mượt path + song song hóa double-reflection) — KHÔNG chứa
    // MeasureCrossSection, chạy nhanh (O(n) hằng số nhỏ), dùng chung cho cả bản đồng bộ
    // (BuildPathInfo, dùng lúc khởi động) lẫn bản coroutine (BuildPathInfoCoroutine, dùng
    // trong rebake nền — cho phép ngắt giữa chừng theo từng điểm đo tiết diện).
    (List<Vector3> smooth, Vector3[] tang, Vector3[] uArr, int n) BuildPathFrame(List<Vector3> rawPoints)
    {
        List<Vector3> smooth = SmoothPath(rawPoints, 5);
        int n = smooth.Count;
        if (n < 2) return (smooth, null, null, n);

        Vector3[] tang = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 a = smooth[Mathf.Max(0, i - 1)];
            Vector3 b = smooth[Mathf.Min(n - 1, i + 1)];
            Vector3 t = b - a;
            tang[i] = t.sqrMagnitude > 1e-10f ? t.normalized : Vector3.up;
        }

        Vector3 u0 = Vector3.Cross(tang[0], Vector3.up);
        if (u0.sqrMagnitude < 1e-6f) u0 = Vector3.Cross(tang[0], Vector3.right);
        u0.Normalize();

        Vector3[] uArr = new Vector3[n];
        uArr[0] = u0;

        // Double reflection method (Wang et al. 2008): song song hóa frame dọc path.
        // Chỉ điểm ĐẦU dùng Vector3.up làm mốc khởi tạo — từ đó trở đi frame được
        // "chuyển tiếp" liên tục, không phụ thuộc up nữa nên không bị lật khi ống
        // gần thẳng đứng hoặc đổi hướng đột ngột ở chỗ rẽ nhánh.
        for (int i = 1; i < n; i++)
        {
            Vector3 p0 = smooth[i - 1], p1 = smooth[i];
            Vector3 t0 = tang[i - 1], t1 = tang[i];
            Vector3 r0 = uArr[i - 1];

            Vector3 v1 = p1 - p0;
            float c1 = Vector3.Dot(v1, v1);
            Vector3 rL, tL;
            if (c1 > 1e-12f)
            {
                rL = r0 - (2f / c1) * Vector3.Dot(v1, r0) * v1;
                tL = t0 - (2f / c1) * Vector3.Dot(v1, t0) * v1;
            }
            else { rL = r0; tL = t0; }

            Vector3 v2 = t1 - tL;
            float c2 = Vector3.Dot(v2, v2);
            Vector3 r1 = c2 > 1e-12f ? rL - (2f / c2) * Vector3.Dot(v2, rL) * v2 : rL;
            r1 = (r1 - t1 * Vector3.Dot(r1, t1));
            if (r1.sqrMagnitude < 1e-8f) r1 = rL; // fallback hiếm gặp, tránh NaN
            uArr[i] = r1.normalized;
        }

        return (smooth, tang, uArr, n);
    }

    // Làm mượt bán kính (lọc trung vị) + gói kết quả thành PathInfo — dùng chung cho cả 2 bản.
    PathInfo FinalizePathInfo(List<Vector3> smooth, Vector3[] tang, Vector3[] uArr, Vector3[] finalPos, float[] finalR, int n)
    {
        var info = new PathInfo();

        // Làm mượt bán kính đã đo bằng LỌC TRUNG VỊ (cửa sổ ±2 điểm), KHÔNG dùng trung bình
        // cộng. Lý do: nếu một điểm rơi đúng vùng mesh biến dạng bất thường lúc animation
        // (phình/xoắn cục bộ), ray-march tại đó có thể đo ra bán kính sai lệch hẳn so với
        // thực tế. Trung bình cộng sẽ kéo giá trị sai đó lan sang CẢ các điểm lân cận vốn
        // nằm ở vùng hình học hoàn toàn bình thường. Trung vị thì không bị 1 giá trị ngoại
        // lệ kéo lệch, nên chỉ điểm bất thường đó bị ảnh hưởng, không lan ra xung quanh.
        float[] smoothR = new float[n];
        float[] windowBuf = new float[5];
        for (int i = 0; i < n; i++)
        {
            int cnt = 0;
            for (int k = -2; k <= 2; k++)
            {
                int idx = i + k;
                if (idx < 0 || idx >= n) continue;
                windowBuf[cnt++] = finalR[idx];
            }
            System.Array.Sort(windowBuf, 0, cnt);
            smoothR[i] = windowBuf[cnt / 2];
        }

        float cum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) cum += Vector3.Distance(finalPos[i - 1], finalPos[i]);
            Vector3 vAxis = Vector3.Cross(tang[i], uArr[i]);
            info.pts.Add(new PathPoint
            {
                pos = finalPos[i],
                t = tang[i],
                u = uArr[i],
                v = vAxis,
                radius = smoothR[i],
                cum = cum
            });
        }
        info.length = cum;
        return info;
    }

    // Bản ĐỒNG BỘ — dùng lúc khởi động (BuildPaths, chạy trong Start(), không có coroutine).
    PathInfo BuildPathInfo(FieldSet fs, List<Vector3> rawPoints)
    {
        var (smooth, tang, uArr, n) = BuildPathFrame(rawPoints);
        if (n < 2) return null;

        float maxSearch = Mathf.Max(fs.bounds.size.x, fs.bounds.size.z);
        Vector3[] finalPos = new Vector3[n];
        float[] finalR = new float[n];
        try
        {
            for (int rep = 0; rep < Mathf.Max(1, computeLoadMultiplier); rep++)
                for (int i = 0; i < n; i++)
                {
                    Vector3 vAxis = Vector3.Cross(tang[i], uArr[i]);
                    var cs = MeasureCrossSection(fs, smooth[i], uArr[i], vAxis, maxSearch);
                    finalPos[i] = cs.pos;
                    finalR[i] = cs.radius;
                }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[VectorFieldBaker] Lỗi khi đo tiết diện: {e}");
        }

        return FinalizePathInfo(smooth, tang, uArr, finalPos, finalR, n);
    }

    // Bản COROUTINE — dùng trong rebake nền. Khác bản đồng bộ ở đúng 1 điểm mấu chốt: kiểm
    // tra ngân sách thời gian (BudgetElapsed) NGAY SAU MỖI ĐIỂM đo tiết diện, không đợi xong
    // hết cả phế nang mới kiểm tra. Trước đây để nguyên 1 phế nang chạy trọn không ngắt —
    // trong lúc .NET/Mono JIT còn "nguội" (chưa tối ưu hot-path MeasureCrossSection), 1 phế
    // nang có thể tốn 150-200ms/khung hình (FPS thấp); càng về sau JIT "nóng máy" dần, cùng
    // phế nang chỉ còn ~40ms/khung hình (FPS tăng dần) — đây chính là nguyên nhân hiện tượng
    // "FPS tăng dần rồi sụt xuống" quan sát được. Ngắt theo từng ĐIỂM (đơn vị nhỏ hơn nhiều so
    // với 1 phế nang) giữ ngân sách ~40ms luôn được tôn trọng chính xác bất kể JIT đang ở
    // trạng thái nào, loại bỏ hẳn nguồn dao động này.
    IEnumerator BuildPathInfoCoroutine(FieldSet fs, List<Vector3> rawPoints, System.Action<PathInfo> onDone)
    {
        var (smooth, tang, uArr, n) = BuildPathFrame(rawPoints);
        if (n < 2) { onDone(null); yield break; }

        float maxSearch = Mathf.Max(fs.bounds.size.x, fs.bounds.size.z);
        Vector3[] finalPos = new Vector3[n];
        float[] finalR = new float[n];

        for (int rep = 0; rep < Mathf.Max(1, computeLoadMultiplier); rep++)
            for (int i = 0; i < n; i++)
            {
                Vector3 vAxis = Vector3.Cross(tang[i], uArr[i]);
                var cs = MeasureCrossSection(fs, smooth[i], uArr[i], vAxis, maxSearch);
                finalPos[i] = cs.pos;
                finalR[i] = cs.radius;

                if (BudgetElapsed())
                    yield return null;
            }

        onDone(FinalizePathInfo(smooth, tang, uArr, finalPos, finalR, n));
    }


    List<Vector3> SmoothPath(List<Vector3> raw, int iterations)
    {
        if (raw.Count < 3) return raw;
        List<Vector3> cur = new List<Vector3>(raw);
        int last = cur.Count - 1;

        // Taubin smoothing (λ dương rồi μ âm, |μ| > λ): lọc nhiễu tần số cao dọc path mà
        // KHÔNG co rút đường cong về phía lõm. Laplacian thường (next = trung bình 2 lân
        // cận) có tính chất toán học giống "curve shortening flow" — càng smoothing nhiều
        // vòng, path càng bị kéo lệch về phía trong của khúc cua thay vì giữ đúng đường
        // tâm thật, biểu hiện ra thành hạt dồn hẳn về 1 bên thành ống như nước chảy ở đoạn
        // hơi cong (ví dụ khí quản). Luân phiên 1 vòng co rồi 1 vòng "phản co" triệt tiêu
        // độ co rút ròng trong khi vẫn giữ hiệu quả lọc nhiễu.
        const float lambda = 0.5f;
        const float mu = -0.53f;

        for (int it = 0; it < iterations; it++)
        {
            cur = TaubinSubPass(cur, lambda);
            cur = TaubinSubPass(cur, mu);

            // 2 điểm biên (gần cửa vào / gần phế nang) trượt nhẹ về phía lân cận mỗi vòng —
            // đúng vùng mesh mỏng/gần biên hở mà việc phân loại trong/ngoài kém tin cậy
            // nhất, nếu giữ nguyên thô sẽ gây hiện tượng "phình - thắt" ở đầu path.
            cur[0] = Vector3.Lerp(cur[0], cur[1], 0.5f);
            cur[last] = Vector3.Lerp(cur[last], cur[last - 1], 0.5f);
        }
        return cur;
    }

    List<Vector3> TaubinSubPass(List<Vector3> cur, float factor)
    {
        List<Vector3> next = new List<Vector3>(cur);
        for (int i = 1; i < cur.Count - 1; i++)
        {
            Vector3 laplacian = (cur[i - 1] + cur[i + 1]) * 0.5f - cur[i];
            next[i] = cur[i] + factor * laplacian;
        }
        return next;
    }

    bool GridInside(FieldSet fs, Vector3 worldPos)
    {
        int xi = Mathf.FloorToInt((worldPos.x - fs.bounds.min.x) / fs.voxelSize.x);
        int yi = Mathf.FloorToInt((worldPos.y - fs.bounds.min.y) / fs.voxelSize.y);
        int zi = Mathf.FloorToInt((worldPos.z - fs.bounds.min.z) / fs.voxelSize.z);
        if (xi < 0 || xi >= fs.rx || yi < 0 || yi >= fs.ry || zi < 0 || zi >= fs.rz) return false;
        return fs.grid[xi, yi, zi];
    }

    // Đo tiết diện ống tại 1 điểm bằng ray-march THẬT trên lưới voxel theo N hướng quanh
    // mặt cắt (u,v). Làm 2 việc:
    //  (1) TÁI TÂM điểm path vào giữa tiết diện thật — lấy trung bình các điểm chạm biên
    //      theo mỗi hướng — vì tâm voxel gốc (từ steepest-descent) có thể lệch tâm, nhất
    //      là gần chỗ rẽ nhánh.
    //  (2) Suy bán kính bằng PHÂN VỊ (percentile) của các khoảng cách đo được, KHÔNG lấy
    //      cực tiểu tuyệt đối — ở độ phân giải voxel thấp, bề mặt bị răng cưa nên chỉ cần
    //      1 tia vô tình chạm góc lồi lõm là đủ kéo giá trị cực tiểu về gần 0, khiến hạt
    //      bị nén thành một sợi mảnh dọc tâm ống thay vì lấp đầy thể tích (đây chính là
    //      nguyên nhân của hiện tượng "particle không chiếm hết volume").
    (Vector3 pos, float radius) MeasureCrossSection(FieldSet fs, Vector3 center, Vector3 u, Vector3 v, float maxSearch)
    {
        float step = Mathf.Min(fs.voxelSize.x, Mathf.Min(fs.voxelSize.y, fs.voxelSize.z)) * 0.5f;
        int rays = crossSectionRays;

        // TÁI TÂM bằng TRUNG BÌNH TỌA ĐỘ CÁC VOXEL "TRONG" LIÊN THÔNG THẬT trong mặt cắt
        // (u,v) quanh center — thay cho cách cũ (trung bình trung điểm các tia bắn theo hướng
        // cố định). Ở nhánh nhỏ (chỉ 1-3 voxel ngang qua), hình dạng voxel "xếp gạch" không đối
        // xứng khiến các tia cố định dễ đo lệch; lấy trung bình trực tiếp trên đúng tập voxel
        // thuộc về nhánh đó (liên thông 2D với center, tránh gom nhầm nhánh khác gần đó) phản
        // ánh đúng trọng tâm hình học thật, không phụ thuộc hướng tia chọn trước.
        //
        // Bước 1: đo nhanh vài tia để ước lượng thô kích thước vùng cần quét (tránh quét cả
        // maxSearch rất lớn một cách lãng phí).
        const int probeRays = 8;
        float quickR = step;
        for (int i = 0; i < probeRays; i++)
        {
            float ang = (i / (float)probeRays) * Mathf.PI * 2f;
            Vector3 dir = u * Mathf.Cos(ang) + v * Mathf.Sin(ang);
            float d = 0f;
            while (d < maxSearch && GridInside(fs, center + dir * (d + step)))
                d += step;
            if (d > quickR) quickR = d;
        }
        float searchRadius = quickR * 1.5f;
        int gridN = Mathf.Clamp(Mathf.CeilToInt(searchRadius / step), 1, 40); // chặn trần tránh quét quá rộng ở chỗ bất thường

        // Bước 2: BFS 2D trong lưới ô vuông (bước = step) trải theo (u,v) quanh center, gom các
        // ô "trong" liên thông với center (hoặc với ô "trong" gần center nhất nếu bản thân
        // center rơi đúng ranh giới/ngoài do làm tròn).
        int size = gridN * 2 + 1;
        bool[,] visited = new bool[size, size];
        var queue = new Queue<Vector2Int>();
        var connected = new List<Vector2Int>();
        Vector2Int mid = new Vector2Int(gridN, gridN);

        Vector2Int startCell = mid;
        bool haveStart = GridInside(fs, center);
        if (!haveStart)
        {
            for (int r = 1; r <= gridN && !haveStart; r++)
            {
                for (int dxg = -r; dxg <= r && !haveStart; dxg++)
                    for (int dyg = -r; dyg <= r && !haveStart; dyg++)
                    {
                        if (Mathf.Max(Mathf.Abs(dxg), Mathf.Abs(dyg)) != r) continue;
                        Vector3 p = center + u * (dxg * step) + v * (dyg * step);
                        if (GridInside(fs, p))
                        {
                            startCell = new Vector2Int(gridN + dxg, gridN + dyg);
                            haveStart = true;
                        }
                    }
            }
        }

        Vector3 recentered;
        if (!haveStart)
        {
            // Không tìm được voxel "trong" nào gần đó — fallback an toàn, giữ nguyên center.
            recentered = center;
        }
        else
        {
            queue.Enqueue(startCell);
            visited[startCell.x, startCell.y] = true;
            int[] ddx = { 1, -1, 0, 0 };
            int[] ddy = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                connected.Add(c);
                for (int d = 0; d < 4; d++)
                {
                    int nx = c.x + ddx[d], ny = c.y + ddy[d];
                    if (nx < 0 || nx >= size || ny < 0 || ny >= size) continue;
                    if (visited[nx, ny]) continue;
                    visited[nx, ny] = true;

                    Vector3 p = center + u * ((nx - gridN) * step) + v * ((ny - gridN) * step);
                    if (GridInside(fs, p))
                        queue.Enqueue(new Vector2Int(nx, ny));
                }
            }

            Vector3 sum = Vector3.zero;
            foreach (var c in connected)
                sum += center + u * ((c.x - gridN) * step) + v * ((c.y - gridN) * step);
            recentered = sum / connected.Count;
        }

        // Bước 3: đo bán kính bằng ray-march (percentile) TỪ tâm mới — giữ nguyên cách đo cũ
        // (đã kiểm chứng ổn), chỉ thay cách xác định tâm.
        Vector3[] dirs = new Vector3[rays];
        float[] ds2 = new float[rays];
        for (int i = 0; i < rays; i++)
        {
            float ang = (i / (float)rays) * Mathf.PI * 2f;
            Vector3 dir = u * Mathf.Cos(ang) + v * Mathf.Sin(ang);
            dirs[i] = dir;
            float d = 0f;
            while (d < maxSearch && GridInside(fs, recentered + dir * (d + step)))
                d += step;
            ds2[i] = d;
        }
        System.Array.Sort(ds2);
        int idx = Mathf.Clamp(Mathf.RoundToInt(ds2.Length * radiusPercentile), 0, ds2.Length - 1);
        // Co bán kính vào gần tâm một chút để an toàn — độc lập với mọi nguyên nhân gây tràn
        // (voxel hóa, frame, recentering...), luôn đảm bảo hạt cách biên thật một khoảng dư.
        float radius = Mathf.Max(ds2[idx] * radiusSafetyShrink, step);

        return (recentered, radius);
    }

    IEnumerator BuildFlowFieldCoroutine(FieldSet fs)
    {
        Debug.Log($"[Flow] BẮT ĐẦU t={Time.realtimeSinceStartup:F1}s rx={fs.rx} ry={fs.ry} rz={fs.rz}");
        fs.flowField = RentVec3Array(fs.rx, fs.ry, fs.rz);

        int[] dx = { 1, -1, 0, 0, 0, 0, 1, 1, -1, -1, 0, 0, 0, 0, 1, -1, 1, -1 };
        int[] dy = { 0, 0, 1, -1, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0 };
        int[] dz = { 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1 };

        int counter = 0;

        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (!fs.grid[x, y, z] || fs.distField[x, y, z] < 0)
                    {
                        fs.flowField[x, y, z] = Vector3.zero;
                    }
                    else
                    {
                        int cd = fs.distField[x, y, z];
                        Vector3 dir = Vector3.zero;

                        for (int d = 0; d < dx.Length; d++)
                        {
                            int nx = x + dx[d], ny = y + dy[d], nz = z + dz[d];
                            if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                            if (!fs.grid[nx, ny, nz] || fs.distField[nx, ny, nz] < 0) continue;

                            int nd = fs.distField[nx, ny, nz];
                            if (nd > cd)
                            {
                                Vector3 step = new Vector3(dx[d], dy[d], dz[d]).normalized;
                                dir += step * (nd - cd);
                            }
                        }

                        fs.flowField[x, y, z] = dir.magnitude > 0.001f ? dir.normalized : Vector3.zero;
                    }

                    counter++;
                    if (counter >= 1000)
                    {
                        counter = 0;
                        if (BudgetElapsed())
                        {
                            _flowYieldCount++;
                            if (_flowYieldCount % 20 == 1)
                                Debug.Log($"[Flow] đang chạy... yield lần {_flowYieldCount}, tại x={x} (/{fs.rx}) t={Time.realtimeSinceStartup:F1}s");
                            yield return null;
                        }
                    }
                }

        Debug.Log($"[Flow] Xong vòng lặp chính t={Time.realtimeSinceStartup:F1}s (tổng {_flowYieldCount} lần yield)");
        yield return StartCoroutine(SmoothFlowFieldCoroutine(fs));
        Debug.Log($"[Flow] Xong smooth lượt 1 t={Time.realtimeSinceStartup:F1}s");
        yield return StartCoroutine(SmoothFlowFieldCoroutine(fs));
        Debug.Log($"[Flow] Xong smooth lượt 2 t={Time.realtimeSinceStartup:F1}s");

        FixInletDirections(fs);
        Debug.Log($"[Flow] HOÀN TẤT t={Time.realtimeSinceStartup:F1}s");
    }

    IEnumerator SmoothFlowFieldCoroutine(FieldSet fs)
    {
        Vector3[,,] tmp = RentVec3Array(fs.rx, fs.ry, fs.rz);
        int counter = 0;

        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (fs.flowField[x, y, z].magnitude < 0.01f) { tmp[x, y, z] = Vector3.zero; }
                    else
                    {
                        Vector3 sum = Vector3.zero; int count = 0;
                        for (int dz2 = -1; dz2 <= 1; dz2++)
                            for (int dy2 = -1; dy2 <= 1; dy2++)
                                for (int dx2 = -1; dx2 <= 1; dx2++)
                                {
                                    int nx = x + dx2, ny = y + dy2, nz = z + dz2;
                                    if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                                    Vector3 nv = fs.flowField[nx, ny, nz];
                                    if (nv.magnitude > 0.01f) { sum += nv; count++; }
                                }
                        tmp[x, y, z] = count > 0 ? (sum / count).normalized : fs.flowField[x, y, z];
                    }

                    counter++;
                    if (counter >= 1000)
                    {
                        counter = 0;
                        if (BudgetElapsed())
                            yield return null;
                    }
                }

        ReturnVec3Array(fs.flowField);
        fs.flowField = tmp;
    }

    void FixInletDirections(FieldSet fs)
    {
        int threshold = 25;
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (!fs.grid[x, y, z] || fs.distField[x, y, z] < 0) continue;
                    if (fs.distField[x, y, z] > threshold) continue;
                    fs.flowField[x, y, z] = Vector3.down;
                }
    }

    // ==================== BẢN ĐỒNG BỘ (chỉ dùng lúc khởi tạo lần đầu ở WaitForVoxelizer) ====================

    void BuildDistanceField(FieldSet fs)
    {
        fs.distField = new int[fs.rx, fs.ry, fs.rz];
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                    fs.distField[x, y, z] = -1;

        Queue<Vector3Int> queue = new Queue<Vector3Int>();
        fs.inletCells.Clear();

        for (int y = fs.ry - 1; y >= 0 && queue.Count == 0; y--)
            for (int x = 0; x < fs.rx; x++)
                for (int z = 0; z < fs.rz; z++)
                    if (fs.grid[x, y, z])
                    {
                        fs.distField[x, y, z] = 0;
                        queue.Enqueue(new Vector3Int(x, y, z));
                        fs.inletCells.Add(new Vector3Int(x, y, z));
                    }

        int[] dx = { 1, -1, 0, 0, 0, 0 };
        int[] dy = { 0, 0, 1, -1, 0, 0 };
        int[] dz = { 0, 0, 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            Vector3Int c = queue.Dequeue();
            int cd = fs.distField[c.x, c.y, c.z];

            for (int d = 0; d < 6; d++)
            {
                int nx = c.x + dx[d], ny = c.y + dy[d], nz = c.z + dz[d];
                if (nx < 0 || nx >= fs.rx) continue;
                if (ny < 0 || ny >= fs.ry) continue;
                if (nz < 0 || nz >= fs.rz) continue;
                if (!fs.grid[nx, ny, nz]) continue;
                if (fs.distField[nx, ny, nz] != -1) continue;

                fs.distField[nx, ny, nz] = cd + 1;
                queue.Enqueue(new Vector3Int(nx, ny, nz));
            }
        }
    }

    void BuildWallDistance(FieldSet fs)
    {
        fs.wallDist = new int[fs.rx, fs.ry, fs.rz];
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                    fs.wallDist[x, y, z] = -1;

        Queue<Vector3Int> queue = new Queue<Vector3Int>();
        int[] dx6 = { 1, -1, 0, 0, 0, 0 };
        int[] dy6 = { 0, 0, 1, -1, 0, 0 };
        int[] dz6 = { 0, 0, 0, 0, 1, -1 };

        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (!fs.grid[x, y, z]) continue;
                    bool nearWall = false;
                    for (int d = 0; d < 6; d++)
                    {
                        int nx = x + dx6[d], ny = y + dy6[d], nz = z + dz6[d];
                        if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz)
                        { nearWall = true; break; }
                        if (!fs.grid[nx, ny, nz]) { nearWall = true; break; }
                    }
                    if (nearWall)
                    {
                        fs.wallDist[x, y, z] = 0;
                        queue.Enqueue(new Vector3Int(x, y, z));
                    }
                }

        while (queue.Count > 0)
        {
            Vector3Int c = queue.Dequeue();
            int cd = fs.wallDist[c.x, c.y, c.z];

            for (int d = 0; d < 6; d++)
            {
                int nx = c.x + dx6[d], ny = c.y + dy6[d], nz = c.z + dz6[d];
                if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                if (!fs.grid[nx, ny, nz]) continue;
                if (fs.wallDist[nx, ny, nz] != -1) continue;

                fs.wallDist[nx, ny, nz] = cd + 1;
                queue.Enqueue(new Vector3Int(nx, ny, nz));
            }
        }

        fs.maxWallDist = 1;
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                    if (fs.wallDist[x, y, z] > fs.maxWallDist)
                        fs.maxWallDist = fs.wallDist[x, y, z];
    }

    void FindAlveoli(FieldSet fs)
    {
        fs.alveoli = new List<Vector3Int>();
        int maxDist = 0;
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                    if (fs.distField[x, y, z] > maxDist) maxDist = fs.distField[x, y, z];

        int minDist = Mathf.RoundToInt(maxDist * 0.35f);

        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (!fs.grid[x, y, z] || fs.distField[x, y, z] < 0) continue;
                    int cd = fs.distField[x, y, z];
                    if (cd < minDist) continue;

                    bool isMax = true;
                    for (int d = 0; d < 26 && isMax; d++)
                    {
                        int nx = x + DX26[d], ny = y + DY26[d], nz = z + DZ26[d];
                        if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                        if (fs.distField[nx, ny, nz] > cd) isMax = false;
                    }
                    if (isMax) fs.alveoli.Add(new Vector3Int(x, y, z));
                }
    }

    void BuildPaths(FieldSet fs)
    {
        fs.paths = new List<PathInfo>();

        foreach (var alv in fs.alveoli)
        {
            List<Vector3> path = new List<Vector3>();
            Vector3Int cur = alv;
            path.Add(GridToWorld(fs, cur.x, cur.y, cur.z));

            int guard = 0;
            while (fs.distField[cur.x, cur.y, cur.z] > 0 && guard < 10000)
            {
                guard++;
                int cd = fs.distField[cur.x, cur.y, cur.z];
                int bestDist = cd;
                Vector3Int best = cur;
                int bestWall = -1;

                for (int d = 0; d < 26; d++)
                {
                    int nx = cur.x + DX26[d], ny = cur.y + DY26[d], nz = cur.z + DZ26[d];
                    if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                    if (fs.distField[nx, ny, nz] < 0) continue;
                    if (fs.distField[nx, ny, nz] < cd)
                    {
                        if (fs.distField[nx, ny, nz] < bestDist ||
                            (fs.distField[nx, ny, nz] == bestDist && fs.wallDist[nx, ny, nz] > bestWall))
                        {
                            bestDist = fs.distField[nx, ny, nz];
                            bestWall = fs.wallDist[nx, ny, nz];
                            best = new Vector3Int(nx, ny, nz);
                        }
                    }
                }

                if (best == cur) break;
                cur = best;
                path.Add(GridToWorld(fs, cur.x, cur.y, cur.z));
            }

            path.Reverse();
            if (path.Count > 1)
            {
                var info = BuildPathInfo(fs, path);
                if (info != null) fs.paths.Add(info);
            }
        }
    }

    void BuildFlowField(FieldSet fs)
    {
        fs.flowField = new Vector3[fs.rx, fs.ry, fs.rz];

        int[] dx = { 1, -1, 0, 0, 0, 0, 1, 1, -1, -1, 0, 0, 0, 0, 1, -1, 1, -1 };
        int[] dy = { 0, 0, 1, -1, 0, 0, 1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0 };
        int[] dz = { 0, 0, 0, 0, 1, -1, 0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1 };

        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (!fs.grid[x, y, z] || fs.distField[x, y, z] < 0)
                    {
                        fs.flowField[x, y, z] = Vector3.zero;
                        continue;
                    }

                    int cd = fs.distField[x, y, z];
                    Vector3 dir = Vector3.zero;

                    for (int d = 0; d < dx.Length; d++)
                    {
                        int nx = x + dx[d], ny = y + dy[d], nz = z + dz[d];
                        if (nx < 0 || nx >= fs.rx) continue;
                        if (ny < 0 || ny >= fs.ry) continue;
                        if (nz < 0 || nz >= fs.rz) continue;
                        if (!fs.grid[nx, ny, nz] || fs.distField[nx, ny, nz] < 0) continue;

                        int nd = fs.distField[nx, ny, nz];
                        if (nd > cd)
                        {
                            Vector3 step = new Vector3(dx[d], dy[d], dz[d]).normalized;
                            dir += step * (nd - cd);
                        }
                    }

                    fs.flowField[x, y, z] = dir.magnitude > 0.001f ? dir.normalized : Vector3.zero;
                }

        SmoothFlowFieldSync(fs); SmoothFlowFieldSync(fs);
        FixInletDirections(fs);
    }

    void SmoothFlowFieldSync(FieldSet fs)
    {
        Vector3[,,] tmp = new Vector3[fs.rx, fs.ry, fs.rz];
        for (int x = 0; x < fs.rx; x++)
            for (int y = 0; y < fs.ry; y++)
                for (int z = 0; z < fs.rz; z++)
                {
                    if (fs.flowField[x, y, z].magnitude < 0.01f) { tmp[x, y, z] = Vector3.zero; continue; }
                    Vector3 sum = Vector3.zero; int count = 0;
                    for (int dz2 = -1; dz2 <= 1; dz2++)
                        for (int dy2 = -1; dy2 <= 1; dy2++)
                            for (int dx2 = -1; dx2 <= 1; dx2++)
                            {
                                int nx = x + dx2, ny = y + dy2, nz = z + dz2;
                                if (nx < 0 || nx >= fs.rx || ny < 0 || ny >= fs.ry || nz < 0 || nz >= fs.rz) continue;
                                Vector3 nv = fs.flowField[nx, ny, nz];
                                if (nv.magnitude > 0.01f) { sum += nv; count++; }
                            }
                    tmp[x, y, z] = count > 0 ? (sum / count).normalized : fs.flowField[x, y, z];
                }
        fs.flowField = tmp;
    }

    Vector3 GridToWorld(FieldSet fs, int x, int y, int z)
    {
        return new Vector3(
            fs.bounds.min.x + (x + 0.5f) * fs.voxelSize.x,
            fs.bounds.min.y + (y + 0.5f) * fs.voxelSize.y,
            fs.bounds.min.z + (z + 0.5f) * fs.voxelSize.z
        );
    }

    // ==================== API ĐỌC CÔNG KHAI — luôn đọc từ _current ====================

    public Vector3 SampleField(Vector3 worldPos)
    {
        var fs = _current;
        if (fs == null) return Vector3.zero;

        int xi = Mathf.FloorToInt((worldPos.x - fs.bounds.min.x) / fs.voxelSize.x);
        int yi = Mathf.FloorToInt((worldPos.y - fs.bounds.min.y) / fs.voxelSize.y);
        int zi = Mathf.FloorToInt((worldPos.z - fs.bounds.min.z) / fs.voxelSize.z);
        if (xi < 0 || xi >= fs.rx || yi < 0 || yi >= fs.ry || zi < 0 || zi >= fs.rz) return Vector3.zero;
        return fs.flowField[xi, yi, zi];
    }

    public float GetWallDistWorld(Vector3 worldPos)
    {
        var fs = _current;
        if (fs == null) return 0f;

        int xi = Mathf.FloorToInt((worldPos.x - fs.bounds.min.x) / fs.voxelSize.x);
        int yi = Mathf.FloorToInt((worldPos.y - fs.bounds.min.y) / fs.voxelSize.y);
        int zi = Mathf.FloorToInt((worldPos.z - fs.bounds.min.z) / fs.voxelSize.z);
        if (xi < 0 || xi >= fs.rx || yi < 0 || yi >= fs.ry || zi < 0 || zi >= fs.rz) return 0f;
        if (fs.wallDist[xi, yi, zi] < 0) return 0f;
        return fs.wallDist[xi, yi, zi] * Mathf.Min(fs.voxelSize.x, fs.voxelSize.z);
    }

    public float GetWallDistNorm(Vector3 worldPos)
    {
        var fs = _current;
        if (fs == null) return 0f;

        int xi = Mathf.FloorToInt((worldPos.x - fs.bounds.min.x) / fs.voxelSize.x);
        int yi = Mathf.FloorToInt((worldPos.y - fs.bounds.min.y) / fs.voxelSize.y);
        int zi = Mathf.FloorToInt((worldPos.z - fs.bounds.min.z) / fs.voxelSize.z);
        if (xi < 0 || xi >= fs.rx || yi < 0 || yi >= fs.ry || zi < 0 || zi >= fs.rz) return 0f;
        if (fs.wallDist[xi, yi, zi] < 0) return 0f;
        return (float)fs.wallDist[xi, yi, zi] / fs.maxWallDist;
    }

    // Dùng cache fs.inletCells thay vì quét lại toàn bộ lớp Y mỗi lần gọi —
    // hàm này được SpawnOne() gọi rất thường xuyên trong pha Hít vào.
    public Vector3 GetRandomInletPoint()
    {
        var fs = _current;
        if (fs == null || fs.inletCells == null || fs.inletCells.Count == 0)
            return fs != null ? fs.bounds.center : Vector3.zero;

        Vector3Int c = fs.inletCells[Random.Range(0, fs.inletCells.Count)];
        return GridToWorld(fs, c.x, c.y, c.z);
    }

    public PathInfo GetRandomPath()
    {
        var fs = _current;
        if (fs == null || fs.paths.Count == 0) return null;
        return fs.paths[Random.Range(0, fs.paths.Count)];
    }

    public void ShowVectorField()
    {
        var fs = _current;
        if (fs == null) return;

        MeshRenderer sourceMR = GetComponent<MeshRenderer>();
        if (sourceMR != null)
        {
            Material mat = sourceMR.material;
            mat.SetFloat("_Mode", 3);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = 3000;
            Color col = mat.color; col.a = 0.1f; mat.color = col;
        }

        if (_fieldMeshGO != null) Destroy(_fieldMeshGO);
        _fieldMeshGO = new GameObject("VectorFieldDebug");

        _arrowMat = new Material(Shader.Find("Standard")); _arrowMat.color = Color.cyan;
        _headMat = new Material(Shader.Find("Standard")); _headMat.color = Color.yellow;

        int arrowCount = 0;
        for (int x = 0; x < fs.rx; x += gizmoStep)
            for (int y = 0; y < fs.ry; y += gizmoStep)
                for (int z = 0; z < fs.rz; z += gizmoStep)
                {
                    Vector3 v = fs.flowField[x, y, z];
                    if (v.magnitude < 0.01f) continue;
                    BuildArrow(GridToWorld(fs, x, y, z), v.normalized, arrowLength);
                    arrowCount++;
                }
        Debug.Log($"Vector field: {arrowCount} arrows.");
    }

    void BuildArrow(Vector3 center, Vector3 dir, float length)
    {
        float shaftLen = length * 0.7f, headLen = length * 0.3f;
        float shaftRadius = length * 0.06f, headRadius = length * 0.15f;
        Quaternion rot = Quaternion.FromToRotation(Vector3.up, dir);
        Vector3 tail = center - dir * length * 0.5f;

        GameObject shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        shaft.transform.SetParent(_fieldMeshGO.transform);
        Destroy(shaft.GetComponent<Collider>());
        shaft.GetComponent<MeshRenderer>().sharedMaterial = _arrowMat;
        shaft.transform.localScale = new Vector3(shaftRadius * 2f, shaftLen * 0.5f, shaftRadius * 2f);
        shaft.transform.position = tail + dir * (shaftLen * 0.5f);
        shaft.transform.rotation = rot;

        GameObject head = new GameObject("Head");
        head.transform.SetParent(_fieldMeshGO.transform);
        MeshFilter mf = head.AddComponent<MeshFilter>();
        MeshRenderer mr = head.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _headMat;
        mf.mesh = GetConeMesh();
        head.transform.localScale = new Vector3(headRadius * 2f, headLen, headRadius * 2f);
        head.transform.position = tail + dir * shaftLen;
        head.transform.rotation = rot;
    }

    Mesh GetConeMesh()
    {
        if (_coneMesh != null) return _coneMesh;
        int segments = 12;
        Vector3[] verts = new Vector3[segments + 2];
        int[] tris = new int[segments * 6];
        verts[0] = new Vector3(0, 1, 0);
        verts[segments + 1] = Vector3.zero;
        for (int i = 0; i < segments; i++)
        {
            float a = (float)i / segments * Mathf.PI * 2f;
            verts[i + 1] = new Vector3(Mathf.Cos(a) * 0.5f, 0, Mathf.Sin(a) * 0.5f);
        }
        int t = 0;
        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            tris[t++] = 0; tris[t++] = i + 1; tris[t++] = next + 1;
            tris[t++] = segments + 1; tris[t++] = next + 1; tris[t++] = i + 1;
        }
        _coneMesh = new Mesh();
        _coneMesh.vertices = verts; _coneMesh.triangles = tris;
        _coneMesh.RecalculateNormals();
        return _coneMesh;
    }

    public void HideVectorField()
    {
        MeshRenderer sourceMR = GetComponent<MeshRenderer>();
        if (sourceMR != null)
        {
            Material mat = sourceMR.material;
            mat.SetFloat("_Mode", 0);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = -1;
            Color col = mat.color; col.a = 1f; mat.color = col;
        }
        if (_fieldMeshGO != null) _fieldMeshGO.SetActive(false);
    }
}