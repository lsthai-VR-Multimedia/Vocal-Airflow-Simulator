using UnityEngine;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ==================== PHA 1 (lúc Play) — XUẤT DỮ LIỆU THÔ, NHẸ ====================
// Chỉ ghi vị trí hạt theo thời gian + mesh đường thở ra file — KHÔNG tính khoảng cách gì cả
// trong lúc này, nên không ảnh hưởng hiệu năng/FPS của mô phỏng đang chạy.
//
// ==================== PHA 2 (sau khi dừng Play, trong Editor) — XỬ LÝ NẶNG ====================
// Bấm nút "Xử lý dữ liệu đã xuất" (do ParticleDataExporterEditor.cs vẽ ra, xem file đó) để
// đọc lại 2 file đã xuất và tính khoảng cách ngắn nhất từng hạt tới mesh — phép tính nặng
// O(số hạt × số tam giác) giờ chạy trong Editor, KHÔNG còn giới hạn bởi việc phải giữ FPS Play
// mode mượt, nên có thể chạy bao lâu cũng được mà không ảnh hưởng gì tới mô phỏng.
public class ParticleDataExporter : MonoBehaviour
{
    [Header("Đầu vào — độc lập, không phụ thuộc script thuật toán nào")]
    [Tooltip("MeshFilter của mesh đường thở GỐC (không phải voxel debug mesh).")]
    [SerializeField] private MeshFilter airwayMesh;

    [Tooltip("ParticleSystem đang hiển thị hạt của thuật toán cần đo (VSP-EL, Confluence...).")]
    [SerializeField] private ParticleSystem targetParticleSystem;

    [Header("Cấu hình xuất dữ liệu (Pha 1)")]
    [SerializeField] private float duration = 30f;
    [SerializeField] private float interval = 0.1f;

    [Header("Giãn thời gian mô phỏng (Time.timeScale)")]
    [Tooltip("Đặt Time.timeScale = giá trị này khi bắt đầu xuất (vd 0.1 = mô phỏng chạy chậm " +
             "10 lần theo giờ thực). Vì Pha 1 giờ chỉ ghi vị trí thô (rất nhẹ), thường KHÔNG " +
             "còn cần thiết như trước — để lại phòng khi cần dùng.")]
    [SerializeField] private float timeScale = 1f;
    [SerializeField] private bool restoreTimeScaleOnDestroy = true;
    private float _originalTimeScale = 1f;

    [Header("Trạng thái Pha 1 (chỉ để xem, không chỉnh)")]
    [SerializeField] private float thoiGianThucDaTroiQua;
    [Tooltip("Tự bật lên true khi ĐÃ XUẤT XONG dữ liệu thô (vị trí hạt + mesh) — tick lên là có " +
             "thể dừng Play, rồi bấm nút xử lý ở Pha 2.")]
    [SerializeField] private bool daXuatFileXong = false;

    [Header("Trạng thái Pha 2 (chỉ để xem, không chỉnh)")]
    [Tooltip("Tự bật lên true khi ĐÃ XỬ LÝ XONG (tính khoảng cách) và ghi ra file kết quả cuối cùng.")]
    [SerializeField] private bool daXuLyXongDuLieu = false;

    private string _particlePosPath;
    private string _meshObjPath;
    private string _resultPath;

    private ParticleSystem.Particle[] _buffer;

    void Start()
    {
        _originalTimeScale = Time.timeScale;
        Time.timeScale = timeScale;
        daXuatFileXong = false;
        daXuLyXongDuLieu = false;
        thoiGianThucDaTroiQua = 0f;

        _particlePosPath = Path.Combine(Application.dataPath, "particle_positions_raw.csv");
        _meshObjPath = Path.Combine(Application.dataPath, "airway_mesh_export.obj");
        _resultPath = Path.Combine(Application.dataPath, "particle_wall_distance_log.csv");

        StartCoroutine(ExportRawData());
    }

    // ==================== PHA 1 ====================

    IEnumerator ExportRawData()
    {
        // Xuất mesh TRƯỚC — chỉ 1 lần, chụp đúng hình dạng tại thời điểm bắt đầu (nếu mesh biến
        // dạng theo animation, đây là hình dạng SNAPSHOT lúc Start, không phải theo từng mốc).
        ExportMeshToObj();
        yield return null;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Time(s),ParticleIndex,X,Y,Z");

        float timer = 0f;
        float realStartTime = Time.realtimeSinceStartup;

        while (timer < duration)
        {
            yield return new WaitForSecondsRealtime(interval);
            thoiGianThucDaTroiQua = Time.realtimeSinceStartup - realStartTime;

            if (targetParticleSystem != null)
            {
                int maxCount = targetParticleSystem.main.maxParticles;
                if (_buffer == null || _buffer.Length < maxCount)
                    _buffer = new ParticleSystem.Particle[Mathf.Max(maxCount, 1)];
                int n = targetParticleSystem.GetParticles(_buffer);

                bool worldSpace = targetParticleSystem.main.simulationSpace == ParticleSystemSimulationSpace.World;
                Transform psT = targetParticleSystem.transform;

                for (int i = 0; i < n; i++)
                {
                    if (_buffer[i].startSize <= 1e-5f) continue; // hạt "vô hình" (đã tắt) — không ghi

                    Vector3 pos = worldSpace ? _buffer[i].position : psT.TransformPoint(_buffer[i].position);
                    sb.AppendLine(
                        timer.ToString("F1", CultureInfo.InvariantCulture) + "," +
                        i + "," +
                        pos.x.ToString("F5", CultureInfo.InvariantCulture) + "," +
                        pos.y.ToString("F5", CultureInfo.InvariantCulture) + "," +
                        pos.z.ToString("F5", CultureInfo.InvariantCulture)
                    );
                }
            }

            timer += interval;
        }

        File.WriteAllText(_particlePosPath, sb.ToString(), new UTF8Encoding(true));
        Debug.Log("[ParticleDataExporter] Đã xuất xong vị trí hạt tại: " + _particlePosPath);
        Debug.Log("[ParticleDataExporter] Đã xuất xong mesh tại: " + _meshObjPath);
        Debug.Log("[ParticleDataExporter] XONG PHA 1 — có thể dừng Play, rồi bấm nút 'Xử lý dữ liệu đã xuất' trong Inspector.");

        daXuatFileXong = true;

        if (restoreTimeScaleOnDestroy)
            Time.timeScale = _originalTimeScale;
    }

    void ExportMeshToObj()
    {
        if (airwayMesh == null)
        {
            Debug.LogError("[ParticleDataExporter] Chưa gán Airway Mesh — không xuất được mesh.");
            return;
        }

        Mesh mesh = airwayMesh.sharedMesh != null ? airwayMesh.sharedMesh : airwayMesh.mesh;
        Vector3[] verts = mesh.vertices;
        int[] tris = mesh.triangles;
        Transform t = airwayMesh.transform;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# Xuất từ ParticleDataExporter — tọa độ THẾ GIỚI (world space), khớp với particle_positions_raw.csv");
        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 w = t.TransformPoint(verts[i]);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F6} {1:F6} {2:F6}", w.x, w.y, w.z));
        }
        for (int i = 0; i < tris.Length; i += 3)
        {
            // OBJ đánh số đỉnh bắt đầu từ 1, không phải 0.
            sb.AppendLine($"f {tris[i] + 1} {tris[i + 1] + 1} {tris[i + 2] + 1}");
        }

        File.WriteAllText(_meshObjPath, sb.ToString(), new UTF8Encoding(true));
    }

    void OnDestroy()
    {
        if (restoreTimeScaleOnDestroy)
            Time.timeScale = _originalTimeScale;
    }

    // ==================== PHA 2 — gọi từ nút bấm trong Editor ====================
    // Đọc lại 2 file đã xuất (particle_positions_raw.csv, airway_mesh_export.obj), tính khoảng
    // cách ngắn nhất từng hạt tới mesh (điểm gần nhất trên tam giác), gộp theo từng mốc thời
    // gian, ghi ra particle_wall_distance_log.csv. Hàm này CHỈ nên chạy khi KHÔNG ở Play mode
    // (dữ liệu đọc từ file đã ghi sẵn, không cần ParticleSystem/mesh nào đang chạy).
    public void ProcessExportedData()
    {
        // Tính lại đường dẫn TRỰC TIẾP ở đây — không dùng _particlePosPath/_meshObjPath (biến
        // instance chỉ được gán trong Start(), mà Start() CHỈ chạy lúc đang Play). Khi bấm nút
        // này sau khi đã DỪNG Play (đúng quy trình thiết kế), Start() không hề chạy lại nên các
        // biến đó vẫn rỗng — khiến File.Exists(null) luôn trả về false dù file thật đã có sẵn.
        string particlePosPath = Path.Combine(Application.dataPath, "particle_positions_raw.csv");
        string meshObjPath = Path.Combine(Application.dataPath, "airway_mesh_export.obj");
        string resultPath = Path.Combine(Application.dataPath, "particle_wall_distance_log.csv");

        if (!File.Exists(particlePosPath) || !File.Exists(meshObjPath))
        {
            Debug.LogError("[ParticleDataExporter] Chưa có đủ file đã xuất — chạy Play để xuất dữ liệu (Pha 1) trước.\n" +
                            $"Đang tìm: {particlePosPath}\n{meshObjPath}");
            return;
        }

        Debug.Log("[ParticleDataExporter] Bắt đầu xử lý dữ liệu (Pha 2)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            LoadObj(meshObjPath, out Vector3[] verts, out int[] tris);

            // Đọc particle_positions_raw.csv, gom theo từng mốc thời gian.
            var byTime = new SortedDictionary<float, List<Vector3>>();
            using (var reader = new StreamReader(particlePosPath))
            {
                string header = reader.ReadLine(); // bỏ dòng tiêu đề
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = line.Split(',');
                    if (parts.Length < 5) continue;

                    float t = float.Parse(parts[0], CultureInfo.InvariantCulture);
                    float x = float.Parse(parts[2], CultureInfo.InvariantCulture);
                    float y = float.Parse(parts[3], CultureInfo.InvariantCulture);
                    float z = float.Parse(parts[4], CultureInfo.InvariantCulture);

                    if (!byTime.TryGetValue(t, out var list))
                    {
                        list = new List<Vector3>();
                        byTime[t] = list;
                    }
                    list.Add(new Vector3(x, y, z));
                }
            }

            StringBuilder outSb = new StringBuilder();
            outSb.AppendLine("Time(s),SoHatHoatDong,KhoangCachTrungBinh,KhoangCachMin,KhoangCachMax");

            int totalDone = 0, totalSamples = 0;
            foreach (var kv in byTime) totalSamples += kv.Value.Count;

            int mocIndex = 0, tongSoMoc = byTime.Count;
            bool bịHủy = false;

            foreach (var kv in byTime)
            {
                mocIndex++;

#if UNITY_EDITOR
                // Cập nhật thanh tiến độ theo TỪNG MỐC thời gian (không phải từng hạt — cập nhật
                // quá thường xuyên tự nó cũng tốn chi phí, làm chậm thêm quá trình xử lý).
                bool huyBoi = EditorUtility.DisplayCancelableProgressBar(
                    "Đang xử lý dữ liệu khoảng cách",
                    $"Mốc {mocIndex}/{tongSoMoc} (t={kv.Key:F1}s) — đã xử lý {totalDone}/{totalSamples} hạt...",
                    (float)mocIndex / tongSoMoc
                );
                if (huyBoi)
                {
                    bịHủy = true;
                    break;
                }
#endif

                float t = kv.Key;
                var positions = kv.Value;

                float sum = 0f, minD = float.MaxValue, maxD = 0f;
                foreach (var p in positions)
                {
                    float d = NearestDistanceToMesh(p, verts, tris);
                    sum += d;
                    if (d < minD) minD = d;
                    if (d > maxD) maxD = d;
                    totalDone++;
                }

                int active = positions.Count;
                float avg = active > 0 ? sum / active : 0f;
                if (active == 0) minD = 0f;

                outSb.AppendLine(
                    t.ToString("F1", CultureInfo.InvariantCulture) + "," +
                    active + "," +
                    avg.ToString("F4", CultureInfo.InvariantCulture) + "," +
                    minD.ToString("F4", CultureInfo.InvariantCulture) + "," +
                    maxD.ToString("F4", CultureInfo.InvariantCulture)
                );
            }

#if UNITY_EDITOR
            EditorUtility.ClearProgressBar();
#endif

            if (bịHủy)
            {
                Debug.LogWarning("[ParticleDataExporter] Đã HỦY xử lý giữa chừng — không ghi file kết quả (dữ liệu chưa đầy đủ).");
                return;
            }

            File.WriteAllText(resultPath, outSb.ToString(), new UTF8Encoding(true));
            sw.Stop();

            Debug.Log($"[ParticleDataExporter] XONG PHA 2 — đã xử lý {totalDone} hạt qua {byTime.Count} mốc thời gian " +
                      $"trong {sw.Elapsed.TotalSeconds:F1}s. Kết quả tại: {resultPath}");
            daXuLyXongDuLieu = true;
        }
        finally
        {
            // Đảm bảo thanh tiến độ LUÔN được dọn dẹp, kể cả khi có lỗi (exception) giữa chừng
            // — tránh bị "kẹt" màn hình progress bar che mất Editor.
#if UNITY_EDITOR
            EditorUtility.ClearProgressBar();
#endif
        }
    }

    void LoadObj(string path, out Vector3[] verts, out int[] tris)
    {
        var vList = new List<Vector3>();
        var tList = new List<int>();

        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith("v "))
            {
                string[] p = line.Substring(2).Trim().Split(' ');
                vList.Add(new Vector3(
                    float.Parse(p[0], CultureInfo.InvariantCulture),
                    float.Parse(p[1], CultureInfo.InvariantCulture),
                    float.Parse(p[2], CultureInfo.InvariantCulture)
                ));
            }
            else if (line.StartsWith("f "))
            {
                string[] p = line.Substring(2).Trim().Split(' ');
                tList.Add(int.Parse(p[0], CultureInfo.InvariantCulture) - 1);
                tList.Add(int.Parse(p[1], CultureInfo.InvariantCulture) - 1);
                tList.Add(int.Parse(p[2], CultureInfo.InvariantCulture) - 1);
            }
        }

        verts = vList.ToArray();
        tris = tList.ToArray();
    }

    static float NearestDistanceToMesh(Vector3 point, Vector3[] verts, int[] tris)
    {
        float best = float.MaxValue;
        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector3 a = verts[tris[i]];
            Vector3 b = verts[tris[i + 1]];
            Vector3 c = verts[tris[i + 2]];
            Vector3 cp = ClosestPointOnTriangle(point, a, b, c);
            float d = (point - cp).sqrMagnitude;
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }

    // Điểm gần nhất trên 1 tam giác (a,b,c) so với điểm p — thuật toán chuẩn (Ericson,
    // "Real-Time Collision Detection", mục 5.1.5).
    static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3);
            return a + v * ab;
        }

        Vector3 cp2 = p - c;
        float d5 = Vector3.Dot(ab, cp2), d6 = Vector3.Dot(ac, cp2);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6);
            return a + w * ac;
        }

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            return b + w * (c - b);
        }

        float denom = 1f / (va + vb + vc);
        float vv = vb * denom;
        float ww = vc * denom;
        return a + ab * vv + ac * ww;
    }
}