using UnityEngine;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(ParticleSystem))]
public class AirwayParticleFlowV2 : MonoBehaviour
{
    [Header("═══ ĐIỀU KHIỂN CHÍNH ═══")]
    [SerializeField] private int particleCount = 15000;
    [SerializeField] private float inhaleTime = 4f;
    [SerializeField] private float exhaleTime = 3f;
    [SerializeField] private float flowSpeed = 3f;

    [Header("═══ KÍCH THƯỚC / MẬT ĐỘ ═══")]
    [SerializeField] private float particleSize = 0.12f;
    [Range(0.3f, 1f)][SerializeField] private float fillRatio = 0.9f;

    [Range(0.5f, 2f)]
    [Tooltip("Số mũ khi sample bán kính lúc sinh hạt (mặc định toán học cho lấp đều diện tích " +
             "là 0.5, tức sqrt(random)). Tăng lên > 0.5 để hạt tập trung gần TÂM ống hơn (giảm " +
             "khả năng ở gần thành/miệng hở), giảm về 0.5 để lấp đều diện tích tuyệt đối. Đánh " +
             "đổi: tăng số này làm giảm độ đồng đều lấp đầy tiết diện (xem chỉ số KS-D trong " +
             "log [Validate]).")]
    [SerializeField] private float radialCenterBias = 1.2f;
    [SerializeField] private float packDensity = 1f;

    [Header("═══ VA CHẠM / DAO ĐỘNG ═══")]
    [SerializeField] private float repulsion = 0.5f;
    [SerializeField] private float jitterAmount = 0.02f;
    [SerializeField] private float jitterSpeed = 1.5f;

    [Header("═══ THÔNG TIN (chỉ đọc) ═══")]
    [SerializeField] private int soHatHienTai = 0;
    [SerializeField] private int sucChua = 0;
    [SerializeField] private float thoiGianPha = 0f;
    [SerializeField] private string pha = "";

    [Header("References")]
    [SerializeField] private VectorFieldBaker vectorField;
    [SerializeField] private ParticleSystem ps;

    private List<VectorFieldBaker.PathInfo> _paths;
    private float[] _pathLen;
    private int[] _cap;
    private int[] _count;
    private int _totalCap;
    private float _maxPathLen;
    private int _numPaths;

    private ParticleSystem.Particle[] _particles;
    private int[] _pathIdx;
    private float[] _s;
    private Vector2[] _off;
    private Vector3[] _pos;
    private byte[] _state;
    private float[] _targetS;
    private Vector2[] _targetUV;
    private Vector2[] _seedOff;   // offset lúc phát hạt (gần cửa vào) — dùng làm điểm đầu để nội suy
    private Vector2[] _repel;     // độ lệch tích lũy do va chạm, tách riêng khỏi nội suy vị trí chính
    private Vector2[] _seed;

    private int _toEmit;
    private float _emitAccum;
    private float _clock;
    private bool _wasExhaling;
    private bool _ready;
    private int _lastRebakeGen = -1;
    private float _emitTargetSmoothed;

    // ==================== ĐÁNH GIÁ TÍNH ĐÚNG CỦA MÔ PHỎNG ====================
    // Tách biệt hoàn toàn khỏi các chỉ số hiệu năng (FPS/thời gian rebake) đã đo trước đó —
    // đây là 2 chỉ số đánh giá TÍNH ĐÚNG hình học của mô phỏng particle:
    //  (1) Containment rate: % hạt đang hoạt động thực sự nằm TRONG lòng ống (theo đúng lưới
    //      voxel hiện tại), lý tưởng phải ~100%. Đo trực tiếp, không suy luận gián tiếp qua FPS.
    //  (2) Fill uniformity: mức độ hạt lấp ĐỀU tiết diện ống. Với cách sample offset đích hiện
    //      tại (góc ngẫu nhiên đều + bán kính = sqrt(random)*R), phân phối bán kính CHUẨN HÓA
    //      r=|off|/R lý thuyết phải có CDF(r)=r² (lấp đều diện tích đĩa tròn). Đo độ lệch giữa
    //      phân phối thực tế và lý thuyết bằng thống kê Kolmogorov–Smirnov (KS-D, càng gần 0
    //      càng khớp).
    [Header("Đánh giá tính đúng mô phỏng (containment + fill uniformity)")]
    [Tooltip("Chu kỳ (giây) giữa mỗi lần đo và log containment rate + fill uniformity.")]
    [SerializeField] private float validationIntervalSec = 1f;
    private float _validationTimer;
    private List<float> _rNormSamples = new List<float>(4096);
    private float _cycleDiagTimer;

    private Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
    private float _cell;
    private float _pSize;

    void Start()
    {
        if (ps == null) ps = GetComponent<ParticleSystem>();
        StartCoroutine(WaitForField());
    }

    IEnumerator WaitForField()
    {
        while (vectorField == null || !vectorField.IsBaked)
            yield return null;

        SnapshotPaths();
        BuildCapacity();
        Init();
        _lastRebakeGen = vectorField.RebakeGeneration;
        _ready = true;
        Debug.Log($"[AirwayParticleFlowV2] SucChua:{_totalCap} Hat:{particleCount} pSize:{_pSize:F4}");
    }

    void SnapshotPaths()
    {
        _numPaths = vectorField.PathCount;
        _paths = new List<VectorFieldBaker.PathInfo>(_numPaths);
        _pathLen = new float[_numPaths];

        for (int i = 0; i < _numPaths; i++)
        {
            var p = vectorField.GetPathAt(i);
            _paths.Add(p);
            _pathLen[i] = p != null ? p.length : 0f;
        }
    }

    // Lấy lại path/bán kính mới nhất sau khi VectorFieldBaker rebake xong 1 thế hệ.
    // Nếu số path KHÔNG đổi (trường hợp thường gặp — cùng số phế nang, chỉ hình học đổi
    // theo animation), giữ nguyên _count[] đang phản ánh số hạt thật sự đang bay trên mỗi
    // path, chỉ tính lại sức chứa (_cap) theo hình học mới — hạt đang bay không bị giật/reset,
    // chỉ đơn giản là từ frame sau sẽ bám theo dữ liệu path mới hơn.
    // Nếu số path đổi (hiếm — số phế nang phát hiện được thay đổi), không thể ánh xạ an toàn
    // particle đang bay sang path mới nên đành reset toàn bộ hệ hạt.
    void RefreshPaths()
    {
        int oldNumPaths = _numPaths;
        var oldCount = _count;

        SnapshotPaths();

        if (_numPaths == oldNumPaths && oldCount != null)
        {
            _count = oldCount;
            RecomputeCapacityOnly();
        }
        else
        {
            Init();
        }
    }

    // Giống phần tính _cap trong BuildCapacity(), nhưng KHÔNG đụng tới _pSize/_cell/_count —
    // giữ nguyên kích thước hạt đã chốt từ lần Init đầu để tránh hạt đổi cỡ đột ngột giữa chừng.
    void RecomputeCapacityOnly()
    {
        float gap0 = particleSize * Mathf.Max(1f, packDensity);
        float f = particleSize / Mathf.Max(0.0001f, _pSize);
        float gap = gap0 / f;

        _cap = new int[_numPaths];
        _totalCap = 0;
        _maxPathLen = 0f;

        for (int i = 0; i < _numPaths; i++)
        {
            var info = _paths[i];
            var pts = info?.pts;
            if (pts == null || pts.Count < 2) { _cap[i] = 0; continue; }

            int capSum = 0;
            for (int k = 0; k < pts.Count - 1; k++)
            {
                float segLen = pts[k + 1].cum - pts[k].cum;
                float R = Mathf.Max(pts[k].radius * fillRatio, _pSize * 0.5f);
                int perRing = Mathf.Max(1, Mathf.FloorToInt((R / gap) * (R / gap) * Mathf.PI));
                int nRings = Mathf.Max(1, Mathf.FloorToInt(segLen / gap));
                capSum += perRing * nRings;
            }
            _cap[i] = capSum;
            _totalCap += capSum;

            if (_pathLen[i] > _maxPathLen) _maxPathLen = _pathLen[i];
        }

        sucChua = _totalCap;
    }

    // Nội suy bán kính AN TOÀN đã được đo trực tiếp bằng ray-march trên lưới voxel
    // (xem VectorFieldBaker.MeasureLocalRadius) tại vị trí cung s dọc theo path pathIdx.
    // Không còn suy từ 1 hằng số toàn cục (maxRadiusWorld) — đó chính là nguyên nhân
    // khiến hạt tràn ra ngoài mặt ống ở các nhánh nhỏ.
    float RadiusAt(int pathIdx, float s)
    {
        var info = _paths[pathIdx];
        var pts = info?.pts;
        if (pts == null || pts.Count == 0) return 0.001f;
        if (pts.Count == 1) return pts[0].radius;

        s = Mathf.Clamp(s, 0f, info.length);
        int lo = 0, hi = pts.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid].cum <= s) lo = mid; else hi = mid;
        }
        var a = pts[lo]; var b = pts[hi];
        float span = Mathf.Max(1e-6f, b.cum - a.cum);
        float t01 = Mathf.Clamp01((s - a.cum) / span);
        return Mathf.Lerp(a.radius, b.radius, t01);
    }

    // Đo 2 chỉ số đánh giá TÍNH ĐÚNG của mô phỏng (không phải hiệu năng): containment rate và
    // fill uniformity. Chạy định kỳ (validationIntervalSec), quét toàn bộ hạt đang hoạt động
    // — đủ rẻ để chạy mỗi giây (không phải mỗi frame) mà không ảnh hưởng FPS.
    void RunValidationDiagnostics()
    {
        if (vectorField == null || _paths == null) return;

        int total = 0, outside = 0;
        _rNormSamples.Clear();

        for (int i = 0; i < particleCount; i++)
        {
            if (_state[i] == 2) continue;
            total++;

            // (1) Containment — hạt có đang thực sự nằm trong lòng ống (theo lưới voxel hiện
            // tại) hay không. Đo TRỰC TIẾP qua vị trí thế giới, không suy luận gián tiếp.
            if (!vectorField.IsWorldPosInsideAirway(_pos[i]))
                outside++;

            // (2) Fill uniformity — thu thập bán kính CHUẨN HÓA r = |off|/R tại vị trí hiện
            // tại của từng hạt, để so sánh với phân phối lý thuyết CDF(r)=r² bên dưới.
            int p = _pathIdx[i];
            float R = RadiusAt(p, _s[i]) * fillRatio;
            if (R > 0.0001f)
                _rNormSamples.Add(Mathf.Clamp01(_off[i].magnitude / R));
        }

        if (total == 0) return;

        float containmentRate = (float)(total - outside) / total * 100f;
        float ksD = ComputeKS_UniformDiscCDF(_rNormSamples);

        Debug.Log($"[Validate] hạt hoạt động: {total} | containment: {containmentRate:F1}% " +
                  $"(ngoài lòng ống: {outside}) | fill-uniformity KS-D: {ksD:F4} " +
                  $"(0 = khớp tuyệt đối phân phối lấp đều diện tích, càng lớn càng lệch)");
    }

    // Thống kê Kolmogorov–Smirnov (KS-D) giữa phân phối thực nghiệm của r_norm và phân phối lý
    // thuyết CDF(r)=r² — đúng phân phối bán kính chuẩn hóa của các điểm được rải ĐỀU trên diện
    // tích 1 đĩa tròn (vì diện tích vành khuyên tại bán kính r tỉ lệ với r, nên CDF tích lũy
    // tỉ lệ với r²). KS-D là độ lệch lớn nhất giữa CDF thực nghiệm và CDF lý thuyết, dao động
    // [0,1] — 0 là khớp tuyệt đối, giá trị lớn cho biết hạt đang dồn cục (thiên lệch) thay vì
    // lấp đều tiết diện.
    float ComputeKS_UniformDiscCDF(List<float> samples)
    {
        if (samples.Count == 0) return float.NaN;
        samples.Sort();
        int n = samples.Count;
        float maxD = 0f;
        for (int i = 0; i < n; i++)
        {
            float empiricalCdf = (float)(i + 1) / n;
            float theoreticalCdf = samples[i] * samples[i];
            float d = Mathf.Abs(empiricalCdf - theoreticalCdf);
            if (d > maxD) maxD = d;
        }
        return maxD;
    }

    void BuildCapacity()
    {
        _cap = new int[_numPaths];
        _count = new int[_numPaths];

        float gap0 = particleSize * Mathf.Max(1f, packDensity);
        long C0 = EstimateTotalCapacity(gap0);

        float f = (C0 > 0 && C0 < particleCount)
            ? Mathf.Pow((float)particleCount / C0, 1f / 3f) * 1.05f
            : 1f;

        _pSize = particleSize / f;
        float gap = gap0 / f;
        _cell = Mathf.Max(0.01f, _pSize * 1.5f);

        _totalCap = 0;
        _maxPathLen = 0f;

        for (int i = 0; i < _numPaths; i++)
        {
            var info = _paths[i];
            var pts = info?.pts;
            if (pts == null || pts.Count < 2) { _cap[i] = 0; continue; }

            int capSum = 0;
            for (int k = 0; k < pts.Count - 1; k++)
            {
                float segLen = pts[k + 1].cum - pts[k].cum;
                float R = Mathf.Max(pts[k].radius * fillRatio, _pSize * 0.5f);

                int perRing = Mathf.Max(1, Mathf.FloorToInt((R / gap) * (R / gap) * Mathf.PI));
                int nRings = Mathf.Max(1, Mathf.FloorToInt(segLen / gap));
                capSum += perRing * nRings;
            }
            _cap[i] = capSum;
            _totalCap += capSum;

            if (_pathLen[i] > _maxPathLen) _maxPathLen = _pathLen[i];
        }

        sucChua = _totalCap;
    }

    long EstimateTotalCapacity(float gap)
    {
        long total = 0;
        for (int i = 0; i < _numPaths; i++)
        {
            var info = _paths[i];
            var pts = info?.pts;
            if (pts == null || pts.Count < 2) continue;
            for (int k = 0; k < pts.Count - 1; k++)
            {
                float segLen = pts[k + 1].cum - pts[k].cum;
                float R = Mathf.Max(pts[k].radius * fillRatio, particleSize * 0.5f);
                int perRing = Mathf.Max(1, Mathf.FloorToInt((R / gap) * (R / gap) * Mathf.PI));
                int nRings = Mathf.Max(1, Mathf.FloorToInt(segLen / gap));
                total += perRing * nRings;
            }
        }
        return total;
    }

    void ResetCounts()
    {
        for (int i = 0; i < _numPaths; i++) _count[i] = 0;
    }

    int AssignTargetPath()
    {
        long sum = 0;
        for (int i = 0; i < _numPaths; i++)
        {
            int r = _cap[i] - _count[i];
            if (r > 0) sum += r;
        }
        if (sum <= 0) return -1;

        long rnd = (long)(Random.value * sum);
        long acc = 0;
        for (int i = 0; i < _numPaths; i++)
        {
            int r = _cap[i] - _count[i];
            if (r > 0)
            {
                acc += r;
                if (rnd <= acc) return i;
            }
        }
        return -1;
    }

    // Nội suy vị trí + frame (T,u,v) trực tiếp từ dữ liệu đã dựng sẵn trong VectorFieldBaker
    // (song song hóa bằng double-reflection, xem BuildPathInfo). Vì u,v của 2 điểm liền kề
    // đã liên tục với nhau, lerp rồi chuẩn hóa lại là đủ mượt — không còn hiện tượng "lật"
    // hướng đột ngột như khi tính Cross(T, up) độc lập tại mỗi điểm.
    void PointFrame(int pathIdx, float s, out Vector3 pos, out Vector3 T, out Vector3 u, out Vector3 v)
    {
        var info = _paths[pathIdx];
        var pts = info.pts;
        int n = pts.Count;

        if (n == 1)
        {
            pos = pts[0].pos; T = pts[0].t; u = pts[0].u; v = pts[0].v;
            return;
        }

        s = Mathf.Clamp(s, 0f, info.length);
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (pts[mid].cum <= s) lo = mid; else hi = mid;
        }

        var a = pts[lo]; var b = pts[hi];
        float span = Mathf.Max(1e-6f, b.cum - a.cum);
        float t01 = Mathf.Clamp01((s - a.cum) / span);

        pos = Vector3.Lerp(a.pos, b.pos, t01);
        T = Vector3.Lerp(a.t, b.t, t01);
        if (T.sqrMagnitude < 1e-10f) T = a.t;
        T.Normalize();

        u = Vector3.Lerp(a.u, b.u, t01);
        u = u - T * Vector3.Dot(u, T);
        if (u.sqrMagnitude < 1e-8f) u = Vector3.Cross(T, Vector3.up);
        if (u.sqrMagnitude < 1e-8f) u = Vector3.Cross(T, Vector3.right);
        u.Normalize();
        v = Vector3.Cross(T, u);
    }

    Vector3 World(int pathIdx, float s, Vector2 off)
    {
        PointFrame(pathIdx, s, out Vector3 pos, out Vector3 T, out Vector3 u, out Vector3 v);
        float R = RadiusAt(pathIdx, s) * fillRatio;
        if (off.magnitude > R) off = off.normalized * R;
        return pos + u * off.x + v * off.y;
    }

    void Init()
    {
        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = Mathf.Infinity;
        main.startSpeed = 0f;
        main.startSize = _pSize;
        main.maxParticles = particleCount;

        var em = ps.emission;
        em.enabled = false;

        _particles = new ParticleSystem.Particle[particleCount];
        _pathIdx = new int[particleCount];
        _s = new float[particleCount];
        _off = new Vector2[particleCount];
        _pos = new Vector3[particleCount];
        _state = new byte[particleCount];
        _targetS = new float[particleCount];
        _targetUV = new Vector2[particleCount];
        _seedOff = new Vector2[particleCount];
        _repel = new Vector2[particleCount];
        _seed = new Vector2[particleCount];

        for (int i = 0; i < particleCount; i++)
        {
            _seed[i] = new Vector2(Random.value * 100f, Random.value * 100f);
            _state[i] = 2;
        }

        ps.Emit(particleCount);
        ps.GetParticles(_particles);

        Vector3 inletPos = vectorField.GetRandomInletPoint();
        for (int i = 0; i < particleCount; i++)
        {
            _particles[i].remainingLifetime = Mathf.Infinity;
            _particles[i].startSize = 0f;
            _particles[i].position = inletPos;
        }
        ps.SetParticles(_particles, particleCount);

        _clock = 0f;
        _toEmit = 0;
        _emitAccum = 0f;
        _emitTargetSmoothed = Mathf.Min(particleCount, _totalCap);
        _wasExhaling = false;
    }

    void EmitOne(int i)
    {
        int p = AssignTargetPath();
        if (p < 0) { _state[i] = 2; return; }

        _count[p]++;
        _pathIdx[i] = p;

        _targetS[i] = Random.value * _pathLen[p];
        float Rt = RadiusAt(p, _targetS[i]) * fillRatio;
        float a = Random.value * Mathf.PI * 2f, rr = Mathf.Pow(Random.value, radialCenterBias);
        _targetUV[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr * Rt;

        _s[i] = 0f;
        float Rin = RadiusAt(p, 0f) * fillRatio;
        float ang = Random.value * Mathf.PI * 2f, r2 = Mathf.Pow(Random.value, radialCenterBias);
        _seedOff[i] = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r2 * Rin;
        _repel[i] = Vector2.zero;
        _off[i] = _seedOff[i];

        _state[i] = 0;
        _pos[i] = World(p, _s[i], _off[i]);
    }

    long Key(int cx, int cy, int cz) => (long)cx * 73856093L ^ (long)cy * 19349663L ^ (long)cz * 83492791L;

    void Update()
    {
        if (!_ready) return;

        // VectorFieldBaker rebake nền LIÊN TỤC theo animation hô hấp, nhưng nếu không lấy
        // lại dữ liệu, hệ hạt sẽ dùng mãi path/bán kính từ lần bake ĐẦU TIÊN — lệch dần so
        // với hình học hiện tại dù ống nhìn "vẫn vậy" (đúng hiện tượng "tự dưng hẹp lại mà
        // ống thì vẫn thế"). Phát hiện khi có thế hệ bake mới hoàn tất và resnapshot lại,
        // KHÔNG reset các hạt đang bay.
        if (vectorField != null && vectorField.IsBaked && vectorField.RebakeGeneration != _lastRebakeGen)
        {
            _lastRebakeGen = vectorField.RebakeGeneration;
            RefreshPaths();
        }

        _clock += Time.deltaTime;

        _validationTimer += Time.deltaTime;
        if (_validationTimer >= validationIntervalSec)
        {
            _validationTimer = 0f;
            RunValidationDiagnostics();
        }

        float cyc = Mathf.Max(0.01f, inhaleTime + exhaleTime);
        float t = Mathf.Repeat(_clock, cyc);
        bool inhaling = t < inhaleTime;
        thoiGianPha = inhaling ? t : t - inhaleTime;
        pha = inhaling ? $"Hít vào ({thoiGianPha:F1}/{inhaleTime:F1}s)" : $"Thở ra ({thoiGianPha:F1}/{exhaleTime:F1}s)";

        // Log chẩn đoán 1 lần/giây — in ra giá trị THẬT của inhaleTime/exhaleTime lúc runtime,
        // để xác nhận chính xác Inspector có đang thực sự truyền đúng giá trị vào hay không,
        // thay vì đoán tiếp.
        _cycleDiagTimer += Time.deltaTime;
        if (_cycleDiagTimer >= 1f)
        {
            _cycleDiagTimer = 0f;
            Debug.Log($"[CycleDiag] _clock={_clock:F1} cyc={cyc:F1} t={t:F1} " +
                      $"inhaleTime={inhaleTime:F1} exhaleTime={exhaleTime:F1} inhaling={inhaling} " +
                      $"toEmit={_toEmit} emitTarget={Mathf.RoundToInt(_emitTargetSmoothed)} totalCap={_totalCap}");
        }

        if (inhaling && _wasExhaling)
        {
            _toEmit = 0; _emitAccum = 0f;
            // Nhảy THẲNG về đúng giá trị mục tiêu ngay lúc bắt đầu chu kỳ mới — lúc này toàn
            // bộ hạt đã reset về vô hình nên một cú nhảy ở đây không tạo cục trắng gì cả.
            // Làm mượt (MoveTowards ở dưới) chỉ cần áp dụng cho các thay đổi _totalCap xảy ra
            // GIỮA CHỪNG chu kỳ (do rebake) — nếu reset về 0 rồi leo dần như bản trước, công
            // thức phát hạt vốn đã tự chia đều theo inhaleTime sẽ bị chia thời gian 2 lần
            // chồng nhau, khiến tổng số hạt phát ra cả chu kỳ chỉ còn khoảng một nửa mục tiêu.
            _emitTargetSmoothed = Mathf.Min(particleCount, _totalCap);
            ResetCounts();
            for (int i = 0; i < particleCount; i++) _state[i] = 2;
        }
        if (!inhaling && !_wasExhaling)
        {
            for (int i = 0; i < particleCount; i++)
                if (_state[i] == 1) _state[i] = 0;
        }
        _wasExhaling = !inhaling;

        float dt = Time.deltaTime, step = flowSpeed * dt;
        ps.GetParticles(_particles);

        // _totalCap được tính lại mỗi khi rebake nền hoàn tất (RefreshPaths ở trên) — vì mesh
        // đang phồng/xẹp theo animation hô hấp, giá trị này có thể nhảy vọt NGAY GIỮA một chu
        // kỳ hít vào, tại một thời điểm không liên quan gì đến tiến độ hít vào hiện tại. Nếu
        // dùng thẳng _totalCap mới nhất để tính emitTarget, tốc độ phát hạt cũng nhảy vọt theo
        // trong khung hình đó, dồn ra một đợt hạt sinh gần như cùng lúc — do di chuyển cùng
        // tốc độ nên chúng bám sát nhau suốt hành trình, nhìn như một cục trắng trôi xuống.
        // Cho emitTarget thích ứng DẦN thay vì nhảy tức thời để tránh đợt dồn này.
        float emitTargetRaw = Mathf.Min(particleCount, _totalCap);
        float maxRatePerSec = particleCount / Mathf.Max(0.01f, inhaleTime);
        _emitTargetSmoothed = Mathf.MoveTowards(_emitTargetSmoothed, emitTargetRaw, maxRatePerSec * dt);
        int emitTarget = Mathf.RoundToInt(_emitTargetSmoothed);
        if (inhaling && _toEmit < emitTarget)
        {
            _emitAccum += (emitTarget / Mathf.Max(0.01f, inhaleTime)) * dt;
            while (_emitAccum >= 1f && _toEmit < emitTarget)
            {
                EmitOne(_toEmit);
                _toEmit++;
                _emitAccum -= 1f;
            }
        }

        _grid.Clear();
        for (int i = 0; i < particleCount; i++)
        {
            if (_state[i] == 2) continue;
            long k = Key(Mathf.FloorToInt(_pos[i].x / _cell), Mathf.FloorToInt(_pos[i].y / _cell), Mathf.FloorToInt(_pos[i].z / _cell));
            if (!_grid.TryGetValue(k, out var lst)) { lst = new List<int>(4); _grid[k] = lst; }
            lst.Add(i);
        }
        float rr2 = _pSize * _pSize;

        int active = 0;
        for (int i = 0; i < particleCount; i++)
        {
            byte st = _state[i];
            if (st == 2) { _particles[i].startSize = 0f; continue; }
            active++;

            Vector3 push = Vector3.zero;
            int near = 0;
            Vector3 pi = _pos[i];
            int cx = Mathf.FloorToInt(pi.x / _cell), cy = Mathf.FloorToInt(pi.y / _cell), cz = Mathf.FloorToInt(pi.z / _cell);

            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (_grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var lst))
                            for (int m = 0; m < lst.Count; m++)
                            {
                                int j = lst[m]; if (j == i) continue;
                                Vector3 d = pi - _pos[j];
                                float sq = d.sqrMagnitude;
                                if (sq < rr2 && sq > 1e-8f)
                                {
                                    float dd = Mathf.Sqrt(sq);
                                    push += d / dd * (_pSize - dd);
                                    near++;
                                }
                            }

            int p = _pathIdx[i];

            if (st == 1)
            {
                PointFrame(p, _s[i], out Vector3 p0, out Vector3 T0, out Vector3 u0, out Vector3 v0);
                if (near > 0)
                {
                    _s[i] = Mathf.Clamp(_s[i] + Vector3.Dot(push, T0) * repulsion * 0.5f, 0f, _pathLen[p]);
                    _repel[i] += new Vector2(Vector3.Dot(push, u0), Vector3.Dot(push, v0)) * repulsion;
                }
                _repel[i] *= 0.9f; // giảm dần, tránh trôi lệch vĩnh viễn khi hết va chạm
                _off[i] = _targetUV[i] + _repel[i];
                _pos[i] = World(p, _s[i], _off[i]);
                _particles[i].position = _pos[i] + Jitter(i);
                _particles[i].startSize = _pSize;
                continue;
            }

            PointFrame(p, _s[i], out Vector3 pos, out Vector3 T, out Vector3 u, out Vector3 v);
            if (near > 0)
                _repel[i] += new Vector2(Vector3.Dot(push, u), Vector3.Dot(push, v)) * repulsion;
            _repel[i] *= 0.9f;

            if (inhaling)
            {
                _s[i] += step;
                if (_s[i] >= _targetS[i])
                {
                    _s[i] = _targetS[i];
                    _state[i] = 1;
                }
            }
            else
            {
                float exStep = (_maxPathLen / Mathf.Max(0.01f, exhaleTime)) * dt;
                _s[i] -= exStep;
                if (_s[i] < 0f)
                {
                    _state[i] = 2;
                    _count[p]--;
                }
            }

            // Nội suy offset (u,v) theo tiến độ s / targetS — TỪ offset lúc phát hạt (seedOff,
            // gần cửa vào) TỚI offset đích (targetUV, đã sample đúng theo bán kính thật tại đích).
            // Trước đây off bị giữ cố định bằng giá trị nhỏ lúc phát hạt suốt cả hành trình rồi
            // mới "nhảy" sang targetUV khi vừa tới đích — khiến các hạt đang di chuyển qua thân
            // chính/đoạn sau carina tụ thành một dải mảnh thay vì lấp đầy tiết diện đi qua, và dải
            // mảnh đó lộ rõ thành vệt bám theo path (path lại hơi lệch tâm ở vùng hợp lưu) thay vì
            // trải đều quanh ống. Công thức dùng chung cho cả hít vào (frac tăng 0→1) và thở ra
            // (frac giảm về 0) vì chỉ phụ thuộc tỉ lệ s/targetS hiện tại.
            if (_state[i] != 2)
            {
                float frac = _targetS[i] > 1e-5f ? Mathf.Clamp01(_s[i] / _targetS[i]) : 1f;
                frac = frac * frac * (3f - 2f * frac); // smoothstep cho mượt
                _off[i] = Vector2.Lerp(_seedOff[i], _targetUV[i], frac) + _repel[i];

                _pos[i] = World(p, _s[i], _off[i]);
                _particles[i].position = _pos[i] + Jitter(i);
                _particles[i].startSize = _pSize;
            }
            else
            {
                _particles[i].startSize = 0f;
            }
        }

        soHatHienTai = active;
        ps.SetParticles(_particles, particleCount);
    }

    Vector3 Jitter(int i)
    {
        if (jitterAmount <= 0f) return Vector3.zero;
        float tt = _clock * jitterSpeed;
        return new Vector3(
            Mathf.PerlinNoise(_seed[i].x, tt) - 0.5f,
            Mathf.PerlinNoise(_seed[i].y, tt) - 0.5f,
            Mathf.PerlinNoise(_seed[i].x + _seed[i].y, tt) - 0.5f
        ) * 2f * jitterAmount;
    }
}