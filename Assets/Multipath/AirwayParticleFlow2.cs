using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// Giai đoạn 2: HÍT VÀO / THỞ RA - mô phỏng hạt từ ngoài vào để thấy rõ quá trình hô hấp.
// - Mỗi hạt được gán ĐÍCH: một vị trí ngẫu nhiên trong lòng MỘT nhánh, chọn theo THỂ TÍCH CÒN TRỐNG
//   (ưu tiên vùng thưa) -> khí lan khắp cây, mật độ tăng DẦN khi hít (thưa trước, dày sau).
// - Hạt chảy từ cổ theo tuyến tới đích rồi đậu (rung tại chỗ); đẩy nhau nhẹ chống đè.
// - Thở ra: hạt rút ngược ra miệng, mật độ giảm dần. Hạt không chết trong phổi.
[RequireComponent(typeof(ParticleSystem))]
public class AirwayParticleFlow2 : MonoBehaviour
{
    [Header("═══ ĐIỀU KHIỂN CHÍNH ═══")]
    [SerializeField] private int particleCount = 15000;
    [SerializeField] private float inhaleTime = 4f;
    [SerializeField] private float exhaleTime = 3f;
    [SerializeField] private float flowSpeed = 3f;

    [Header("═══ KÍCH THƯỚC / MẬT ĐỘ ═══")]
    [SerializeField] private float particleSize = 0.12f;
    [Range(0.3f, 1f)][SerializeField] private float fillRatio = 0.9f;
    [Tooltip(">=1: o thua hon. Khoang cach dich toi thieu ~ particleSize*packDensity.")]
    [SerializeField] private float packDensity = 1f;

    [Header("═══ VA CHẠM / DAO ĐỘNG ═══")]
    [SerializeField] private float repulsion = 0.5f;
    [SerializeField] private float jitterAmount = 0.02f;
    [SerializeField] private float jitterSpeed = 1.5f;

    [Header("═══ THÔNG TIN ═══")]
    [SerializeField] private int soHatHienTai = 0;
    [SerializeField] private int sucChua = 0;
    [Tooltip("Thoi gian trong pha hien tai (giay).")]
    [SerializeField] private float thoiGianPha = 0f;
    [Tooltip("Pha hien tai.")]
    [SerializeField] private string pha = "";

    [Header("References")]
    [SerializeField] private AirwayPathBuilder airway;
    [SerializeField] private ParticleSystem ps;

    // sức chứa theo nhánh (thể tích)
    private int[] _cap, _countEdge, _capUnder, _countUnder;
    private int _totalCap;

    // hạt
    private ParticleSystem.Particle[] _particles;
    private int[][] _route; private int[] _ridx;
    private int[] _tEdge; private float[] _tS; private Vector2[] _tUV;
    private int[] _edge; private float[] _s; private Vector2[] _off; private Vector3[] _pos;
    private byte[] _state;               // 0=chảy, 1=đậu, 2=ẩn
    private Vector2[] _seed;
    private int _toEmit; private float _emitAccum;
    private float _clock; private bool _wasExhaling; private bool _ready;

    private IReadOnlyList<AirwayPathBuilder.FlowNode> _nodes;
    private IReadOnlyList<AirwayPathBuilder.FlowEdge> _edges;
    private int _root, _inletEdge;
    private float[] _edgeLen, _edgeR;
    private int[] _edgeSource, _parentEdge;

    private Dictionary<long, List<int>> _grid = new Dictionary<long, List<int>>();
    private float _cell; private float _pSize;   // kích thước hạt hiệu dụng (tự co để đủ số)

    void Start()
    {
        if (ps == null) ps = GetComponent<ParticleSystem>();
        if (airway == null) airway = FindObjectOfType<AirwayPathBuilder>();
        StartCoroutine(WaitForBuild());
    }
    IEnumerator WaitForBuild()
    {
        while (airway == null || !airway.IsBuilt || airway.FlowEdges == null || airway.FlowEdges.Count == 0)
            yield return null;
        _nodes = airway.FlowNodes; _edges = airway.FlowEdges; _root = airway.FlowRoot;
        BuildTree(); BuildCapacity(); Init();
        _ready = true;
        Debug.Log($"[AirwayParticleFlow2] SucChua:{_totalCap} Hat:{particleCount}");
    }

    void BuildTree()
    {
        int ne = _edges.Count, nn = _nodes.Count;
        _edgeLen = new float[ne]; _edgeR = new float[ne];
        _edgeSource = new int[ne]; _parentEdge = new int[nn];
        for (int i = 0; i < nn; i++) _parentEdge[i] = -1;
        for (int e = 0; e < ne; e++)
        {
            var ed = _edges[e]; float L = 0f;
            for (int k = 0; k < ed.pts.Length - 1; k++) L += Vector3.Distance(ed.pts[k], ed.pts[k + 1]);
            _edgeLen[e] = L;
            var prof = ed.radiiFill != null ? ed.radiiFill : ed.radii;
            float rs = 0f; int rc = 0; if (prof != null) foreach (var r in prof) { rs += r; rc++; }
            _edgeR[e] = rc > 0 ? rs / rc : particleSize;
        }
        for (int nd = 0; nd < nn; nd++) foreach (int e in _nodes[nd].edges) { _edgeSource[e] = nd; _parentEdge[_edges[e].to] = e; }
        _inletEdge = _nodes[_root].edges.Count > 0 ? _nodes[_root].edges[0] : 0;
    }

    // sức chứa mỗi nhánh = số hạt xếp không đè; TỰ CO kích thước hạt để tổng >= particleCount
    void BuildCapacity()
    {
        int ne = _edges.Count;
        _cap = new int[ne]; _countEdge = new int[ne]; _capUnder = new int[ne]; _countUnder = new int[ne];

        // pass 1: sức chứa ở kích thước cấu hình
        float gap0 = particleSize * Mathf.Max(1f, packDensity);
        long C0 = 0;
        for (int e = 0; e < ne; e++)
        {
            float L = _edgeLen[e], R = Mathf.Max(_edgeR[e] * fillRatio, particleSize * 0.5f);
            C0 += (long)Mathf.Max(1, Mathf.FloorToInt(L / gap0)) * Mathf.Max(1, Mathf.FloorToInt((R / gap0) * (R / gap0) * 3.1416f));
        }
        // nếu thiếu chỗ -> thu nhỏ hạt (sức chứa ~ 1/kích thước^3)
        float f = (C0 > 0 && C0 < particleCount) ? Mathf.Pow((float)particleCount / C0, 1f / 3f) * 1.05f : 1f;
        _pSize = particleSize / f;
        float gap = gap0 / f;
        _cell = Mathf.Max(0.01f, _pSize * 1.5f);

        // pass 2: sức chứa ở kích thước hiệu dụng
        _totalCap = 0;
        for (int e = 0; e < ne; e++)
        {
            float L = _edgeLen[e], R = Mathf.Max(_edgeR[e] * fillRatio, _pSize * 0.5f);
            int perRing = Mathf.Max(1, Mathf.FloorToInt((R / gap) * (R / gap) * 3.1416f));
            int nRings = Mathf.Max(1, Mathf.FloorToInt(L / gap));
            _cap[e] = perRing * nRings; _totalCap += _cap[e];
        }
        foreach (int e in _nodes[_root].edges) RecurseCap(e);
        sucChua = _totalCap;
        // độ sâu lớn nhất (arc gốc->cuối cạnh xa nhất) để pace thở ra đúng exhaleTime
        _maxDepth = 0f;
        foreach (int e in _nodes[_root].edges) CalcArc(e, 0f);
    }
    private float _maxDepth;
    void CalcArc(int e, float arcStart)
    {
        float arcEnd = arcStart + _edgeLen[e];
        if (arcEnd > _maxDepth) _maxDepth = arcEnd;
        foreach (int c in _nodes[_edges[e].to].edges) CalcArc(c, arcEnd);
    }
    int RecurseCap(int e)
    {
        int sum = _cap[e];
        foreach (int c in _nodes[_edges[e].to].edges) sum += RecurseCap(c);
        _capUnder[e] = sum; return sum;
    }
    void ResetCounts()
    {
        for (int e = 0; e < _edges.Count; e++) { _countEdge[e] = 0; _countUnder[e] = 0; }
    }
    void Reserve(int e)   // giữ 1 chỗ ở cạnh e + cộng dồn lên tổ tiên
    {
        _countEdge[e]++;
        int cur = e; while (cur >= 0) { _countUnder[cur]++; int src = _edgeSource[cur]; cur = _parentEdge[src]; }
    }

    int[] RouteToEdge(int e)
    {
        var list = new List<int>(); int cur = e;
        while (true) { list.Add(cur); int src = _edgeSource[cur]; if (src == _root || _parentEdge[src] < 0) break; cur = _parentEdge[src]; }
        list.Reverse(); return list.ToArray();
    }

    // Gán ĐÍCH: đi xuống theo THỂ TÍCH CÒN TRỐNG (ưu tiên vùng thưa); trả về cạnh đích, hoặc -1 nếu cây đầy
    int AssignTarget()
    {
        int node = _root, chosen = -1, guard = 0;
        while (guard++ < 500)
        {
            var ch = _nodes[node].edges;
            float sum = 0f;
            for (int k = 0; k < ch.Count; k++) { int r = _capUnder[ch[k]] - _countUnder[ch[k]]; if (r > 0) sum += r; }
            if (sum <= 0f) break;                       // dưới đây hết chỗ
            float rnd = Random.value * sum, a = 0f; int pick = -1;
            for (int k = 0; k < ch.Count; k++) { int r = _capUnder[ch[k]] - _countUnder[ch[k]]; if (r > 0) { a += r; if (rnd <= a) { pick = ch[k]; break; } } }
            if (pick < 0) { for (int k = 0; k < ch.Count; k++) if (_capUnder[ch[k]] - _countUnder[ch[k]] > 0) { pick = ch[k]; break; } }
            chosen = pick; node = _edges[pick].to;
            int remHere = _cap[chosen] - _countEdge[chosen];
            int remSub = _capUnder[chosen] - _countUnder[chosen];
            bool leaf = _nodes[node].edges.Count == 0;
            if (remHere > 0 && (leaf || remSub <= remHere || Random.value < (float)remHere / remSub)) break;  // đậu tại đây
        }
        if (chosen >= 0 && _cap[chosen] - _countEdge[chosen] > 0) return chosen;
        return -1;
    }

    void Init()
    {
        var main = ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = Mathf.Infinity; main.startSpeed = 0f;
        main.startSize = _pSize; main.maxParticles = particleCount;
        var em = ps.emission; em.enabled = false;

        _particles = new ParticleSystem.Particle[particleCount];
        _route = new int[particleCount][]; _ridx = new int[particleCount];
        _tEdge = new int[particleCount]; _tS = new float[particleCount]; _tUV = new Vector2[particleCount];
        _edge = new int[particleCount]; _s = new float[particleCount]; _off = new Vector2[particleCount]; _pos = new Vector3[particleCount];
        _state = new byte[particleCount]; _seed = new Vector2[particleCount];
        for (int i = 0; i < particleCount; i++) { _seed[i] = new Vector2(Random.value * 100f, Random.value * 100f); _state[i] = 2; }
        ps.Emit(particleCount);
        ps.GetParticles(_particles);
        for (int i = 0; i < particleCount; i++) { _particles[i].remainingLifetime = Mathf.Infinity; _particles[i].startSize = 0f; _particles[i].position = _nodes[_root].pos; }
        ps.SetParticles(_particles, particleCount);
        _clock = 0f; _toEmit = 0; _emitAccum = 0f; _wasExhaling = false;
    }

    void PointFrame(int e, float s, out Vector3 pos, out Vector3 T, out Vector3 u, out Vector3 v)
    {
        var pts = _edges[e].pts; int n = pts.Length;
        float acc = 0f; int seg = 0; pos = pts[0];
        for (int k = 0; k < n - 1; k++)
        {
            float sg = Vector3.Distance(pts[k], pts[k + 1]);
            if (acc + sg >= s) { float t = (s - acc) / Mathf.Max(1e-6f, sg); pos = Vector3.Lerp(pts[k], pts[k + 1], t); seg = k; break; }
            acc += sg; pos = pts[k + 1]; seg = k;
        }
        T = (seg < n - 1) ? (pts[seg + 1] - pts[seg]) : (pts[n - 1] - pts[n - 2]);
        if (T.sqrMagnitude < 1e-10f) T = Vector3.up; T.Normalize();
        u = Vector3.Cross(T, Vector3.up); if (u.sqrMagnitude < 1e-6f) u = Vector3.Cross(T, Vector3.right);
        u.Normalize(); v = Vector3.Cross(T, u);
    }
    float RadiusAt(int e, float s)
    {
        var prof = _edges[e].radiiFill != null ? _edges[e].radiiFill : _edges[e].radii;
        if (prof == null || prof.Length == 0) return _edgeR[e];
        float f = _edgeLen[e] > 1e-6f ? Mathf.Clamp01(s / _edgeLen[e]) : 0f;
        return prof[Mathf.Clamp(Mathf.RoundToInt(f * (prof.Length - 1)), 0, prof.Length - 1)];
    }
    Vector3 World(int e, float s, Vector2 off)
    {
        PointFrame(e, s, out Vector3 pos, out Vector3 T, out Vector3 u, out Vector3 v);
        float R = RadiusAt(e, s) * fillRatio; if (off.magnitude > R) off = off.normalized * R;
        return pos + u * off.x + v * off.y;
    }

    void EmitOne(int i, float sStart)
    {
        int te = AssignTarget();
        if (te < 0) { _state[i] = 2; return; }        // cây đầy -> chưa sinh
        Reserve(te);
        _tEdge[i] = te;
        _tS[i] = Random.value * _edgeLen[te];
        float Rt = RadiusAt(te, _tS[i]) * fillRatio;
        float a = Random.value * Mathf.PI * 2f, rr = Mathf.Sqrt(Random.value);
        _tUV[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr * Rt;
        _route[i] = RouteToEdge(te); _ridx[i] = 0;
        _edge[i] = _route[i][0]; _s[i] = sStart;
        float ang = Random.value * Mathf.PI * 2f, r2 = Mathf.Sqrt(Random.value);
        float Rin = RadiusAt(_edge[i], 0f) * fillRatio;
        _off[i] = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r2 * Rin;
        _state[i] = 0;
        _pos[i] = World(_edge[i], _s[i], _off[i]);
    }

    long Key(int cx, int cy, int cz) => (long)cx * 73856093L ^ (long)cy * 19349663L ^ (long)cz * 83492791L;

    void Update()
    {
        if (!_ready) return;
        _clock += Time.deltaTime;
        float cyc = Mathf.Max(0.01f, inhaleTime + exhaleTime);
        float t = Mathf.Repeat(_clock, cyc);
        bool inhaling = t < inhaleTime;
        thoiGianPha = inhaling ? t : t - inhaleTime;
        pha = inhaling ? $"Hit vao ({thoiGianPha:F1}/{inhaleTime:F1}s)" : $"Tho ra ({thoiGianPha:F1}/{exhaleTime:F1}s)";

        if (inhaling && _wasExhaling) { _toEmit = 0; _emitAccum = 0f; ResetCounts(); for (int i = 0; i < particleCount; i++) _state[i] = 2; }
        if (!inhaling && !_wasExhaling) { for (int i = 0; i < particleCount; i++) if (_state[i] == 1) _state[i] = 0; }  // thở ra: đậu -> chảy ngược
        _wasExhaling = !inhaling;

        float dt = Time.deltaTime, step = flowSpeed * dt;
        ps.GetParticles(_particles);

        // SINH ĐỀU: tốc độ cố định; tổng = min(particleCount, sức chứa)
        int emitTarget = Mathf.Min(particleCount, _totalCap);
        if (inhaling && _toEmit < emitTarget)
        {
            _emitAccum += (emitTarget / Mathf.Max(0.01f, inhaleTime)) * dt;
            float front = Mathf.Max(step, _pSize * 2f);
            while (_emitAccum >= 1f && _toEmit < emitTarget) { EmitOne(_toEmit, Random.value * front); _toEmit++; _emitAccum -= 1f; }
        }

        // lưới va chạm
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

            // va chạm (đẩy nhẹ) cho cả hạt chảy lẫn đậu -> không đè, dày lên đều
            Vector3 push = Vector3.zero; int near = 0; Vector3 pi = _pos[i];
            int cx = Mathf.FloorToInt(pi.x / _cell), cy = Mathf.FloorToInt(pi.y / _cell), cz = Mathf.FloorToInt(pi.z / _cell);
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
                        if (_grid.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var lst))
                            for (int m = 0; m < lst.Count; m++)
                            {
                                int j = lst[m]; if (j == i) continue;
                                Vector3 d = pi - _pos[j]; float sq = d.sqrMagnitude;
                                if (sq < rr2 && sq > 1e-8f) { float dd = Mathf.Sqrt(sq); push += d / dd * (_pSize - dd); near++; }
                            }

            if (st == 1)   // ĐẬU: đẩy nhau cả DỌC lẫn NGANG (relaxation) -> tãi đều, không đè
            {
                PointFrame(_edge[i], _s[i], out Vector3 p0, out Vector3 T0, out Vector3 u0, out Vector3 v0);
                if (near > 0)
                {
                    _s[i] = Mathf.Clamp(_s[i] + Vector3.Dot(push, T0) * repulsion * 0.5f, 0f, _edgeLen[_edge[i]]);
                    _off[i] += new Vector2(Vector3.Dot(push, u0), Vector3.Dot(push, v0)) * repulsion;
                }
                _pos[i] = World(_edge[i], _s[i], _off[i]);
                _particles[i].position = _pos[i] + Jitter(i);
                _particles[i].startSize = _pSize;
                continue;
            }

            // st == 0: ĐANG CHẢY (chỉ đẩy NGANG, không đẩy dọc -> luôn tiến qua ngã ba, không khựng)
            PointFrame(_edge[i], _s[i], out Vector3 pos, out Vector3 T, out Vector3 u, out Vector3 v);
            if (near > 0)
                _off[i] += new Vector2(Vector3.Dot(push, u), Vector3.Dot(push, v)) * repulsion;
            _off[i] *= 0.95f;   // kéo về tâm 5% mỗi khung -> hạt không dạt ra thành ống khi di chuyển

            if (inhaling)
            {
                _s[i] += step;
                bool onTarget = _ridx[i] >= _route[i].Length - 1;
                if (onTarget)
                {
                    if (_s[i] >= _tS[i]) { _s[i] = _tS[i]; _off[i] = _tUV[i]; _state[i] = 1; }   // tới đích -> đậu
                }
                else if (_s[i] >= _edgeLen[_edge[i]])
                {
                    _s[i] -= _edgeLen[_edge[i]]; _ridx[i]++; _edge[i] = _route[i][_ridx[i]];       // sang cạnh kế trên tuyến
                }
            }
            else   // THỞ RA: chảy ngược ra miệng, pace để hạt sâu nhất ra đúng lúc hết exhaleTime
            {
                float exStep = (_maxDepth / Mathf.Max(0.01f, exhaleTime)) * dt;
                _s[i] -= exStep;
                if (_s[i] < 0f)
                {
                    if (_ridx[i] <= 0) { _state[i] = 2; }                                          // ra miệng
                    else { _ridx[i]--; _edge[i] = _route[i][_ridx[i]]; _s[i] += _edgeLen[_edge[i]]; }
                }
            }

            if (_state[i] != 2) { _pos[i] = World(_edge[i], _s[i], _off[i]); _particles[i].position = _pos[i] + Jitter(i); _particles[i].startSize = _pSize; }
            else _particles[i].startSize = 0f;
        }
        soHatHienTai = active;
        ps.SetParticles(_particles, particleCount);
    }

    Vector3 Jitter(int i)
    {
        if (jitterAmount <= 0f) return Vector3.zero;
        float tt = _clock * jitterSpeed;
        return new Vector3(Mathf.PerlinNoise(_seed[i].x, tt) - 0.5f,
                           Mathf.PerlinNoise(_seed[i].y, tt) - 0.5f,
                           Mathf.PerlinNoise(_seed[i].x + _seed[i].y, tt) - 0.5f) * 2f * jitterAmount;
    }
}