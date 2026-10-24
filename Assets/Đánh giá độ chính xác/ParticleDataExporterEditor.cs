using UnityEngine;
using UnityEditor;

// QUAN TRỌNG: file này PHẢI nằm trong 1 thư mục tên đúng là "Editor" (viết hoa/thường không
// quan trọng, nhưng tên thư mục phải là "Editor") ở đâu đó trong Assets — đây là quy ước bắt
// buộc của Unity để nhận diện code chỉ chạy trong Editor (dùng namespace UnityEditor), không bị
// đóng gói vào bản build cuối cùng (build sẽ lỗi biên dịch nếu để lẫn ngoài thư mục Editor, vì
// UnityEditor không tồn tại trong build).
//
// Ví dụ đường dẫn hợp lệ: Assets/Đánh giá độ chính xác/Editor/ParticleDataExporterEditor.cs
[CustomEditor(typeof(ParticleDataExporter))]
public class ParticleDataExporterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // Vẽ toàn bộ các field bình thường (duration, interval, timeScale, các checkbox...)
        // y hệt Inspector mặc định — không mất gì cả, chỉ thêm nút bên dưới.
        DrawDefaultInspector();

        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Pha 2 — Xử lý dữ liệu (chạy trong Editor, không cần Play)", EditorStyles.boldLabel);

        ParticleDataExporter exporter = (ParticleDataExporter)target;

        if (Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Đang ở chế độ Play — nên DỪNG Play trước khi bấm nút xử lý, để không tranh " +
                "chấp CPU với mô phỏng đang chạy (dù về mặt kỹ thuật vẫn bấm được lúc đang Play).",
                MessageType.Warning);
        }

        if (GUILayout.Button("Xử lý dữ liệu đã xuất", GUILayout.Height(32)))
        {
            exporter.ProcessExportedData();
        }

        EditorGUILayout.HelpBox(
            "Đọc lại 2 file đã xuất ở Pha 1 (particle_positions_raw.csv, airway_mesh_export.obj) " +
            "trong Assets, tính khoảng cách ngắn nhất từng hạt tới mesh, ghi kết quả ra " +
            "particle_wall_distance_log.csv. Có thể chạy lâu tùy số hạt/số tam giác — theo dõi " +
            "tiến độ qua Console.",
            MessageType.Info);
    }
}
