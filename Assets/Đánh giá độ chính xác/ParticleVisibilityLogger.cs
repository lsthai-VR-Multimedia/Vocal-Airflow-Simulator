using UnityEngine;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections;

// Script ĐỘC LẬP, tách hoàn toàn khỏi LSTFPS.cs và mọi code thuật toán (Voxelizer,
// VectorFieldBaker, AirwayParticleFlow...). Chỉ dùng 2 API chuẩn của Unity:
//   - ParticleSystem.GetParticles() — đọc vị trí hạt của BẤT KỲ thuật toán nào, miễn là hạt
//     đó được render qua 1 component ParticleSystem thật (không cần biết code bên trong).
//   - Camera depth texture (Built-in Render Pipeline) — đọc độ sâu đã render ra hình ảnh,
//     không dùng Physics.Raycast/Collider gì cả.
//
// Nguyên lý: nếu mesh ống được render ĐẶC (không phải particle Additive xuyên thấu), 1 hạt
// thực sự nằm TRONG lòng ống sẽ luôn bị thành ống (gần camera hơn) che khuất trên depth
// buffer. Hạt TRÀN RA NGOÀI sẽ không có gì che, vẫn "nhìn thấy" được. Dùng 6 camera bao quanh
// (trái/phải/trước/sau/trên/dưới), 1 hạt được tính là "nhìn thấy" (tràn ra ngoài) nếu ÍT NHẤT
// 1 trong 6 camera nhìn thấy nó (phép hợp — OR), không phải trung bình cộng.
//
// GIỚI HẠN đã biết (ghi rõ để không diễn giải quá mức số liệu):
//  1. Các hốc lõm bị che khuất từ CẢ 6 hướng trục chính (hiếm) vẫn có thể bị BỎ SÓT dù hạt
//     đã thực sự tràn ra ngoài — đây là chỉ số "phát hiện được", không phải "đầy đủ tuyệt đối".
//  2. Hạt ở gần 2 đầu ống HỞ (cửa vào khí quản, đầu các phế nang) sẽ "nhìn thấy" dù đang ở
//     đúng vị trí hợp lệ ngay sát mép trong — bị TÍNH NHẦM là tràn ra ngoài.
//  3. Vật liệu particle PHẢI có depth test/depth write (không phải Additive/Unlit Transparent
//     đang dùng để nhìn xuyên ống trong lúc quan sát) — nếu không, mọi hạt luôn "nhìn thấy"
//     bất kể có vật cản hay không, phép đo vô nghĩa.
public class ParticleVisibilityLogger : MonoBehaviour
{
    [Header("Đầu vào — kéo thả, không phụ thuộc script thuật toán nào")]
    [Tooltip("ParticleSystem đang hiển thị hạt của thuật toán cần đo (VSP-EL, Confluence...).")]
    [SerializeField] private ParticleSystem targetParticleSystem;

    [Tooltip("6 camera bao quanh mô hình (trái/phải/trước/sau/trên/dưới). Không bắt buộc đúng 6 " +
             "— có thể để nhiều/ít hơn, phép hợp (OR) vẫn áp dụng đúng với số lượng camera bạn đặt.")]
    [SerializeField] private Camera[] cameras;

    [Header("Cấu hình ghi log")]
    [SerializeField] private float duration = 30f;
    [SerializeField] private float interval = 0.1f;

    [Tooltip("Độ phân giải texture đọc depth cho mỗi camera — không cần bằng độ phân giải màn " +
             "hình thật, chỉ cần đủ để tách đúng pixel ứng với từng hạt. 512 là mức hợp lý.")]
    [SerializeField] private int depthTextureSize = 512;

    [Tooltip("Sai số cho phép (world unit) khi so sánh độ sâu hạt với độ sâu cảnh — tránh hạt " +
             "nằm SÁT ngay mặt trong thành ống (đúng vị trí clamp bởi fillRatio) bị tính nhầm là " +
             "bị che do sai số làm tròn độ sâu.")]
    [SerializeField] private float depthEpsilon = 0.02f;

    private string filePath;
    private StringBuilder sb = new StringBuilder();
    private Material _copyDepthMat;
    private RenderTexture[] _depthRTs;
    private Texture2D _readTex;
    private ParticleSystem.Particle[] _buffer;

    void Start()
    {
        filePath = Path.Combine(Application.dataPath, "particle_visibility_log.csv");
        sb.AppendLine("Time(s),SoHatHoatDong,SoHatNhinThay,TiLePhanTram");

        _copyDepthMat = new Material(Shader.Find("Hidden/CopyCameraDepth"));
        if (_copyDepthMat.shader == null || !_copyDepthMat.shader.isSupported)
            Debug.LogError("[ParticleVisibilityLogger] Không tìm thấy/không hỗ trợ shader " +
                            "'Hidden/CopyCameraDepth' — cần đặt file CopyCameraDepth.shader " +
                            "vào project (thư mục bất kỳ, miễn trong Assets).");

        _depthRTs = new RenderTexture[cameras != null ? cameras.Length : 0];
        for (int i = 0; i < _depthRTs.Length; i++)
        {
            _depthRTs[i] = new RenderTexture(depthTextureSize, depthTextureSize, 24, RenderTextureFormat.RFloat);
            _depthRTs[i].Create();
        }
        _readTex = new Texture2D(depthTextureSize, depthTextureSize, TextureFormat.RFloat, false);

        if (cameras != null)
            foreach (var cam in cameras)
                if (cam != null) cam.depthTextureMode |= DepthTextureMode.Depth;

        StartCoroutine(LogVisibility());
    }

    IEnumerator LogVisibility()
    {
        float timer = 0f;
        while (timer < duration)
        {
            yield return new WaitForSeconds(interval);

            int active = 0, visible = 0;
            if (targetParticleSystem != null && cameras != null && cameras.Length > 0)
                CountVisibleParticles(out active, out visible);

            float pct = active > 0 ? (float)visible / active * 100f : 0f;
            sb.AppendLine(
                timer.ToString("F1", CultureInfo.InvariantCulture) + "," +
                active + "," +
                visible + "," +
                pct.ToString("F2", CultureInfo.InvariantCulture)
            );

            timer += interval;
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        Debug.Log("[ParticleVisibilityLogger] Đã ghi xong file tại: " + filePath);
    }

    void CountVisibleParticles(out int active, out int visible)
    {
        active = 0;
        visible = 0;

        int maxCount = targetParticleSystem.main.maxParticles;
        if (_buffer == null || _buffer.Length < maxCount)
            _buffer = new ParticleSystem.Particle[Mathf.Max(maxCount, 1)];
        int n = targetParticleSystem.GetParticles(_buffer);
        if (n == 0) return;

        // GetParticles() trả về TOÀN BỘ mảng hạt Unity biết tới (kích thước cố định theo cấu
        // hình ParticleSystem), không phải số hạt đang thực sự "hoạt động" theo logic riêng
        // của thuật toán — nhiều thuật toán (như AirwayParticleFlow) không xóa hạt khỏi hệ
        // thống khi hạt "vô hình", chỉ đặt startSize=0 để ẩn đi. Cần lọc theo startSize > 0 để
        // đếm đúng số hạt đang thực sự hiển thị/hoạt động, không phải đếm nhầm toàn bộ mảng.
        const float sizeEpsilon = 1e-5f;
        active = 0;
        for (int i = 0; i < n; i++)
            if (_buffer[i].startSize > sizeEpsilon)
                active++;

        // Lấy sẵn depth buffer của từng camera 1 lần cho toàn bộ n hạt (không render lại theo
        // từng hạt) — render + đọc depth mỗi camera đúng 1 lần mỗi mốc lấy mẫu (0.1s).
        var depthPixels = new Color[cameras.Length][];
        for (int c = 0; c < cameras.Length; c++)
        {
            Camera cam = cameras[c];
            if (cam == null) { depthPixels[c] = null; continue; }

            cam.Render(); // đảm bảo _CameraDepthTexture (global) ứng đúng với camera này tại đây
            Graphics.Blit(null, _depthRTs[c], _copyDepthMat);

            RenderTexture.active = _depthRTs[c];
            _readTex.ReadPixels(new Rect(0, 0, depthTextureSize, depthTextureSize), 0, 0);
            _readTex.Apply(false);
            RenderTexture.active = null;

            depthPixels[c] = _readTex.GetPixels();
        }

        Vector3 worldPos = Vector3.zero;
        bool worldSpace = targetParticleSystem.main.simulationSpace == ParticleSystemSimulationSpace.World;
        Transform psTransform = targetParticleSystem.transform;

        bool loggedSample = false;
        for (int i = 0; i < n; i++)
        {
            if (_buffer[i].startSize <= sizeEpsilon) continue; // hạt "vô hình" (state đã tắt) — không tính

            worldPos = worldSpace ? _buffer[i].position : psTransform.TransformPoint(_buffer[i].position);

            bool seenByAny = false;
            for (int c = 0; c < cameras.Length && !seenByAny; c++)
            {
                Camera cam = cameras[c];
                if (cam == null || depthPixels[c] == null) continue;

                Vector3 sp = cam.WorldToScreenPoint(worldPos);
                if (sp.z <= 0f) continue; // sau lưng camera đó — bỏ qua, xét camera khác
                if (sp.x < 0f || sp.x >= cam.pixelWidth || sp.y < 0f || sp.y >= cam.pixelHeight) continue;

                int px = Mathf.Clamp((int)(sp.x / cam.pixelWidth * depthTextureSize), 0, depthTextureSize - 1);
                int py = Mathf.Clamp((int)(sp.y / cam.pixelHeight * depthTextureSize), 0, depthTextureSize - 1);
                float sceneDepth = depthPixels[c][py * depthTextureSize + px].r;

                float particleEyeDepth = Vector3.Dot(worldPos - cam.transform.position, cam.transform.forward);

                // LOG CHẨN ĐOÁN — chỉ in 1 mẫu duy nhất mỗi lần lấy mẫu (0.1s), đủ để kiểm tra
                // giá trị thô có hợp lý không (particleEyeDepth và sceneDepth phải cùng đơn vị
                // world unit, cùng bậc độ lớn — nếu sceneDepth luôn ra 0, 1, hoặc số rất lớn bất
                // thường (vd hàng nghìn), là dấu hiệu shader/RenderTexture bị lỗi, không phải
                // lỗi logic so sánh).
                if (!loggedSample)
                {
                    loggedSample = true;
                    Debug.Log($"[ParticleVisibilityLogger][Chẩn đoán] cam={cam.name} " +
                              $"particleEyeDepth={particleEyeDepth:F3} sceneDepth={sceneDepth:F3} " +
                              $"pixel=({px},{py}) screenPos=({sp.x:F0},{sp.y:F0})");
                }

                // Hạt là vật gần nhất tại đúng pixel đó (không có gì che trước nó) → nhìn thấy được.
                if (particleEyeDepth <= sceneDepth + depthEpsilon)
                    seenByAny = true;
            }

            if (seenByAny) visible++;
        }
    }

    void OnDestroy()
    {
        if (_depthRTs != null)
            foreach (var rt in _depthRTs)
                if (rt != null) rt.Release();
    }
}