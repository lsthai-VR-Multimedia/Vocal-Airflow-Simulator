using UnityEngine;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections;

// Script ĐỘC LẬP, tách hoàn toàn khỏi mọi code thuật toán (Voxelizer, VectorFieldBaker,
// AirwayParticleFlow...) và khỏi ParticleVisibilityLogger.cs. Chỉ dùng 2 nguồn dữ liệu chuẩn:
//   - Mesh gốc của đường thở (vertices/triangles), đọc trực tiếp qua MeshFilter — KHÔNG qua
//     bất kỳ voxel hóa/xử lý trung gian nào của thuật toán đang được đánh giá.
//   - Vị trí hạt qua ParticleSystem.GetParticles() (API chuẩn Unity, dùng chung được cho mọi
//     thuật toán miễn hạt render qua 1 ParticleSystem thật).
//
// Với mỗi hạt, khoảng cách tới thành ống = khoảng cách NGẮN NHẤT tới BẤT KỲ tam giác nào của
// mesh (không phải khoảng cách theo 1 hướng cố định) — dùng thuật toán điểm gần nhất trên tam
// giác chuẩn (Ericson, "Real-Time Collision Detection", mục 5.1.5).
//
// Ý nghĩa: khoảng cách trung bình này định lượng mức độ "hạt bám sát tâm ống" — càng lớn,
// hạt càng co gọn về tâm (an toàn hơn nhưng mô phỏng trông mỏng/kém chân thực hơn); càng nhỏ,
// hạt càng lấp gần sát thành ống (chân thực hơn nhưng dễ tràn ra ngoài hơn). Dùng cùng lúc với
// ParticleVisibilityLogger (đo tỉ lệ tràn) để đánh giá đúng điểm cân bằng giữa 2 yếu tố đối
// nghịch này, thay vì chỉ nhìn 1 phía.
//
// LƯU Ý HIỆU NĂNG: đây là phép tính O(số hạt × số tam giác) — với mesh nhiều tam giác và
// nhiều hạt, 1 lần lấy mẫu có thể mất đáng kể thời gian thực (không chỉ 1 khung hình), khiến
// nhịp ghi log thực tế chậm hơn "interval" cấu hình. Đây là thí nghiệm TÁCH RIÊNG khỏi đo FPS
// nên chấp nhận được, nhưng cần biết trước để không hiểu lầm là lỗi.
public class ParticleWallDistanceLogger : MonoBehaviour
{
    [Header("Đầu vào — độc lập, không phụ thuộc script thuật toán nào")]
    [Tooltip("MeshFilter của mesh đường thở GỐC (không phải voxel debug mesh) — đọc thẳng vertices/triangles.")]
    [SerializeField] private MeshFilter airwayMesh;

    [Tooltip("ParticleSystem đang hiển thị hạt của thuật toán cần đo (VSP-EL, Confluence...).")]
    [SerializeField] private ParticleSystem targetParticleSystem;

    [Header("Cấu hình ghi log")]
    [SerializeField] private float duration = 30f;
    [SerializeField] private float interval = 0.1f;

    [Header("Giãn thời gian mô phỏng (Time.timeScale)")]
    [Tooltip("Đặt Time.timeScale = giá trị này khi bắt đầu đo (ví dụ 0.1 = mô phỏng chạy chậm " +
             "10 lần theo giờ thực). Cho phép tính toán (voxelize, rebake...) có nhiều thời gian " +
             "thực hơn để hoàn tất trong mỗi khoảng thời gian mô phỏng, giảm ảnh hưởng của độ trễ " +
             "tính toán lên kết quả đo. Để 1 = không đổi gì (tốc độ bình thường).")]
    [SerializeField] private float timeScale = 1f;

    [Tooltip("Trả Time.timeScale về 1 khi script này bị hủy/dừng — tránh quên trả lại, ảnh " +
             "hưởng tới các lần chạy sau trong cùng phiên Play.")]
    [SerializeField] private bool restoreTimeScaleOnDestroy = true;

    private float _originalTimeScale = 1f;

    [Header("Trạng thái (chỉ để xem, không chỉnh)")]
    [Tooltip("Thời gian THỰC (giờ đồng hồ, không bị timeScale ảnh hưởng) đã trôi qua kể từ lúc bắt đầu ghi log — tự cập nhật liên tục lúc đang Play.")]
    [SerializeField] private float thoiGianThucDaTroiQua;

    [Tooltip("Tự bật lên true ngay khi file CSV đã được ghi xong — kiểm tra ô này thay vì phải mở Console.")]
    [SerializeField] private bool daXuatFileXong = false;

    private string filePath;
    private StringBuilder sb = new StringBuilder();
    private ParticleSystem.Particle[] _buffer;

    private Vector3[] _worldVerts;
    private int[] _triangles;

    void Start()
    {
        _originalTimeScale = Time.timeScale;
        Time.timeScale = timeScale;
        daXuatFileXong = false;
        thoiGianThucDaTroiQua = 0f;

        filePath = Path.Combine(Application.dataPath, "particle_wall_distance_log.csv");
        sb.AppendLine("Time(s),SoHatHoatDong,KhoangCachTrungBinh,KhoangCachMin,KhoangCachMax");

        if (airwayMesh != null)
        {
            Mesh mesh = airwayMesh.sharedMesh != null ? airwayMesh.sharedMesh : airwayMesh.mesh;
            Vector3[] localVerts = mesh.vertices;
            _triangles = mesh.triangles;

            // Snapshot 1 lần lúc Start — nếu mesh biến dạng theo animation hô hấp, khoảng cách
            // đo được là so với hình dạng TẠI THỜI ĐIỂM SNAPSHOT này, không cập nhật theo thời
            // gian thực. Đủ dùng để đánh giá xu hướng chung; nếu cần đúng hình dạng tức thời
            // tại mỗi mốc 0.1s, cần đọc lại mesh.vertices mỗi lần lấy mẫu (tốn thêm chi phí).
            _worldVerts = new Vector3[localVerts.Length];
            Transform t = airwayMesh.transform;
            for (int i = 0; i < localVerts.Length; i++)
                _worldVerts[i] = t.TransformPoint(localVerts[i]);
        }
        else
        {
            Debug.LogError("[ParticleWallDistanceLogger] Chưa gán Airway Mesh — không thể đo.");
        }

        StartCoroutine(LogDistances());
    }

    IEnumerator LogDistances()
    {
        float timer = 0f;
        float realStartTime = Time.realtimeSinceStartup;
        while (timer < duration)
        {
            yield return new WaitForSecondsRealtime(interval);

            thoiGianThucDaTroiQua = Time.realtimeSinceStartup - realStartTime;

            int active = 0;
            float sum = 0f, minD = float.MaxValue, maxD = 0f;

            if (targetParticleSystem != null && _worldVerts != null && _triangles != null)
            {
                int maxCount = targetParticleSystem.main.maxParticles;
                if (_buffer == null || _buffer.Length < maxCount)
                    _buffer = new ParticleSystem.Particle[Mathf.Max(maxCount, 1)];
                int n = targetParticleSystem.GetParticles(_buffer);

                bool worldSpace = targetParticleSystem.main.simulationSpace == ParticleSystemSimulationSpace.World;
                Transform psT = targetParticleSystem.transform;

                for (int i = 0; i < n; i++)
                {
                    if (_buffer[i].startSize <= 1e-5f) continue; // hạt "vô hình" (đã tắt) — không tính
                    active++;

                    Vector3 pos = worldSpace ? _buffer[i].position : psT.TransformPoint(_buffer[i].position);
                    float d = NearestDistanceToMesh(pos);

                    sum += d;
                    if (d < minD) minD = d;
                    if (d > maxD) maxD = d;
                }
            }

            float avg = active > 0 ? sum / active : 0f;
            if (active == 0) minD = 0f;

            sb.AppendLine(
                timer.ToString("F1", CultureInfo.InvariantCulture) + "," +
                active + "," +
                avg.ToString("F4", CultureInfo.InvariantCulture) + "," +
                minD.ToString("F4", CultureInfo.InvariantCulture) + "," +
                maxD.ToString("F4", CultureInfo.InvariantCulture)
            );

            timer += interval;
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        Debug.Log("[ParticleWallDistanceLogger] Đã ghi xong file tại: " + filePath);
        daXuatFileXong = true;

        if (restoreTimeScaleOnDestroy)
            Time.timeScale = _originalTimeScale;
    }

    void OnDestroy()
    {
        if (restoreTimeScaleOnDestroy)
            Time.timeScale = _originalTimeScale;
    }

    // Khoảng cách NGẮN NHẤT từ 1 điểm tới TOÀN BỘ mesh — duyệt qua mọi tam giác, lấy nhỏ nhất.
    // Đơn giản, đúng, nhưng O(số tam giác) mỗi lần gọi — xem ghi chú hiệu năng ở đầu file.
    float NearestDistanceToMesh(Vector3 point)
    {
        float best = float.MaxValue;
        for (int i = 0; i < _triangles.Length; i += 3)
        {
            Vector3 a = _worldVerts[_triangles[i]];
            Vector3 b = _worldVerts[_triangles[i + 1]];
            Vector3 c = _worldVerts[_triangles[i + 2]];
            Vector3 cp = ClosestPointOnTriangle(point, a, b, c);
            float d = (point - cp).sqrMagnitude; // so sánh bình phương khoảng cách, đỡ tốn sqrt
            if (d < best) best = d;
        }
        return Mathf.Sqrt(best);
    }

    // Điểm gần nhất trên 1 tam giác (a,b,c) so với điểm p — thuật toán chuẩn (Ericson,
    // "Real-Time Collision Detection", mục 5.1.5), xét đủ các trường hợp: gần đỉnh, gần cạnh,
    // gần mặt trong tam giác.
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