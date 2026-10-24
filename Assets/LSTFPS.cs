using UnityEngine;
using UnityEngine.Profiling;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System;

public class LSTFPS : MonoBehaviour
{
    [Header("Cấu hình ghi log")]
    [SerializeField] private float duration = 30f;   // tổng thời gian ghi log (giây)
    [SerializeField] private float interval = 0.1f;  // khoảng thời gian mỗi lần lấy mẫu (giây) — chỉ là mục tiêu danh nghĩa, thời gian THỰC đo riêng

    [Header("Cấu hình SPARC")]
    [SerializeField] private float sparcCutoffFreq = 5f;   // fc: tần số cắt (Hz). LƯU Ý: không nên vượt quá fs/2 (Nyquist). Với fs=10Hz (interval=0.1s), fs/2=5Hz.
    [SerializeField] private float sparcAmpThreshold = 0.05f; // amp_th: ngưỡng biên độ chuẩn hóa
    [SerializeField] private int sparcPadLevel = 4;          // số bậc zero-padding cho FFT (nfft = 2^(ceil(log2(N))+padLevel))
    [Tooltip("Áp cửa sổ Hann trước khi FFT để giảm rò rỉ phổ (spectral leakage) do biên tín hiệu " +
             "không liên tục (đầu/cuối 30s không bằng nhau). Khuyến nghị bật (true).")]
    [SerializeField] private bool useHannWindow = true;

    private string filePath;
    private int frameCount = 0;
    private StringBuilder sb = new StringBuilder();
    private List<float> fpsSamples = new List<float>(); // lưu lại toàn bộ chuỗi FPS để tính SPARC cuối cùng

    private const float BYTES_TO_MB = 1f / (1024f * 1024f);

    void Start()
    {
        // Ghi vào thư mục Assets của project (giống Application.dataPath trong bản gốc)
        // LƯU Ý: chỉ hoạt động khi chạy trong Unity Editor.
        // Khi build ra thiết bị thật (Android/iOS/PC standalone), thư mục Assets
        // không tồn tại hoặc không có quyền ghi -> nên đổi lại Application.persistentDataPath.
        filePath = Path.Combine(Application.dataPath, "fps_log.csv");

        // Dùng dấu phẩy để Excel nhận đúng định dạng CSV
        // Total Allocated: tổng bộ nhớ Mono/IL2CPP đã cấp phát (managed heap)
        // Reserved: tổng bộ nhớ hệ thống đã cấp cho Unity (kể cả chưa dùng hết)
        // Mono Used: bộ nhớ managed (C# objects) đang thực sự được dùng
        // ElapsedReal(s): thời gian THỰC đã trôi qua kể từ mẫu trước (khác Time(s) danh nghĩa
        //   khi có giật — cột này mới phản ánh đúng mẫu đó "nặng" tới mức nào).
        sb.AppendLine("Time(s),FPS,ElapsedReal(s),Total Allocated (MB),Reserved (MB),Mono Used (MB)");

        StartCoroutine(LogFPS());
    }

    void Update()
    {
        frameCount++;
    }

    IEnumerator LogFPS()
    {
        float timer = 0f;

        // Đo THỜI GIAN THỰC đã trôi qua giữa 2 lần lấy mẫu bằng Time.realtimeSinceStartup,
        // KHÔNG giả định mỗi lần lấy mẫu đúng bằng "interval" danh nghĩa. Trước đây
        // "fps = frameCount / interval" chia cho 0.1 CỐ ĐỊNH — nếu có 1 frame duy nhất mất
        // rất lâu (vd 500ms, do đúng lúc xử lý 1 khối nặng của thuật toán), thời gian thực
        // giữa 2 lần lấy mẫu đã vượt xa 0.1s từ lâu, nhưng code vẫn chia cho 0.1 khiến FPS
        // báo ra trông "đỡ tệ hơn" thực tế, và làm loãng/che mất đúng cú giật cần thấy.
        float lastSampleTime = Time.realtimeSinceStartup;

        while (timer < duration)
        {
            yield return new WaitForSeconds(interval);

            float now = Time.realtimeSinceStartup;
            float elapsedReal = now - lastSampleTime; // thời gian THỰC, có thể lớn hơn nhiều so với interval nếu vừa có giật
            lastSampleTime = now;

            float fps = elapsedReal > 0f ? frameCount / elapsedReal : 0f;
            fpsSamples.Add(fps); // lưu lại để tính SPARC sau khi kết thúc 30s

            // Lấy số liệu bộ nhớ tại thời điểm lấy mẫu (đơn vị: byte -> đổi ra MB)
            float totalAllocatedMB = Profiler.GetTotalAllocatedMemoryLong() * BYTES_TO_MB;
            float totalReservedMB = Profiler.GetTotalReservedMemoryLong() * BYTES_TO_MB;
            float monoUsedMB = Profiler.GetMonoUsedSizeLong() * BYTES_TO_MB;

            // Dùng InvariantCulture để tránh lỗi dấu phẩy/chấm thập phân theo ngôn ngữ máy
            sb.AppendLine(
                timer.ToString("F1", CultureInfo.InvariantCulture) + "," +
                fps.ToString("F2", CultureInfo.InvariantCulture) + "," +
                elapsedReal.ToString("F3", CultureInfo.InvariantCulture) + "," +
                totalAllocatedMB.ToString("F2", CultureInfo.InvariantCulture) + "," +
                totalReservedMB.ToString("F2", CultureInfo.InvariantCulture) + "," +
                monoUsedMB.ToString("F2", CultureInfo.InvariantCulture)
            );

            frameCount = 0;
            timer += interval;
        }

        // Chỉ tính và ghi SPARC theo Cách 1 (delta-FPS: v(t) = FPS(t) - FPS(t-1)) — bỏ hẳn
        // phần thống kê Min/Max/Avg FPS, Min/Max/Avg bộ nhớ, và SPARC Cách 2 theo yêu cầu.
        if (fpsSamples.Count > 0)
        {
            float fs = 1f / interval; // tần số lấy mẫu (Hz), ví dụ interval=0.1s -> fs=10Hz

            float[] deltaFps = new float[fpsSamples.Count - 1];
            for (int i = 0; i < deltaFps.Length; ++i)
                deltaFps[i] = fpsSamples[i + 1] - fpsSamples[i];
            float sparcDelta = ComputeSPARC(deltaFps, fs, sparcPadLevel, sparcCutoffFreq, sparcAmpThreshold, useHannWindow);

            sb.AppendLine();
            sb.AppendLine("SPARC - Cách 1: delta-FPS (FPS[t]-FPS[t-1])," + sparcDelta.ToString("F4", CultureInfo.InvariantCulture));
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true)); // true = ghi kèm BOM để Excel nhận đúng UTF-8
        Debug.Log("Đã ghi xong file FPS tại: " + filePath);
    }

    // ==========================================================================
    // SPARC (Spectral Arc Length) - bản cải tiến 2015 (Balasubramanian et al.)
    // Tham khảo cài đặt tham chiếu (Python): "movement" đóng vai trò v(t)
    // ==========================================================================
    /// <summary>
    /// Tính SPARC cho một tín hiệu (ở đây là chuỗi FPS đóng vai trò v(t)).
    /// </summary>
    /// <param name="movement">Chuỗi giá trị tín hiệu (FPS theo thời gian)</param>
    /// <param name="fs">Tần số lấy mẫu (Hz)</param>
    /// <param name="padLevel">Số bậc zero-padding: nfft = 2^(ceil(log2(N)) + padLevel)</param>
    /// <param name="fc">Tần số cắt (Hz)</param>
    /// <param name="ampTh">Ngưỡng biên độ chuẩn hóa để xác định băng tần hiệu quả</param>
    /// <param name="applyHannWindow">Áp cửa sổ Hann trước FFT để giảm rò rỉ phổ do biên tín hiệu</param>
    /// <returns>Giá trị SPARC (số âm; càng gần 0 càng mượt)</returns>
    private float ComputeSPARC(float[] movement, float fs, int padLevel, float fc, float ampTh, bool applyHannWindow)
    {
        int n = movement.Length;
        if (n < 4)
        {
            Debug.LogWarning("SPARC: không đủ dữ liệu để tính (cần tối thiểu 4 mẫu).");
            return float.NaN;
        }

        // 1. Xác định kích thước FFT (zero-padding lên lũy thừa của 2)
        int nfft = NextPow2(n);
        for (int i = 0; i < padLevel; ++i) nfft *= 2;

        // 2. Loại bỏ thành phần DC (trừ giá trị trung bình) trước khi FFT.
        //    Lý do: v(t) trong công thức gốc là hồ sơ vận tốc dao động quanh 0.
        //    FPS(t) luôn dương và có trung bình lớn (vd ~60), nếu đưa thẳng vào FFT,
        //    thành phần DC (tần số 0Hz) sẽ áp đảo phổ, tạo ra một "cú rơi dốc đứng"
        //    ngay đầu phổ sau khi chuẩn hóa -> làm sai lệch nghiêm trọng arc length,
        //    khiến tín hiệu càng ổn định (dao động nhỏ quanh trung bình) lại càng
        //    cho SPARC âm hơn một cách giả tạo (nghịch với trực giác).
        float mean = 0f;
        for (int i = 0; i < n; ++i) mean += movement[i];
        mean /= n;

        // 3. Chuẩn bị mảng phức (real, imag) đã trừ DC, áp cửa sổ Hann (nếu bật), zero-pad phần còn lại
        float[] real = new float[nfft];
        float[] imag = new float[nfft];
        for (int i = 0; i < n; ++i)
        {
            float centered = movement[i] - mean;
            if (applyHannWindow)
            {
                // Cửa sổ Hann: w[i] = 0.5 * (1 - cos(2*pi*i/(N-1)))
                // Giảm dần biên độ về 0 ở hai đầu đoạn tín hiệu, làm tín hiệu "liên tục hơn"
                // khi FFT giả định lặp lại tuần hoàn -> giảm rò rỉ phổ (spectral leakage).
                float w = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (n - 1)));
                centered *= w;
            }
            real[i] = centered;
        }
        // phần còn lại mặc định = 0 (zero padding)

        // 4. FFT
        FFT(real, imag);

        // 5. Phổ biên độ chuẩn hóa (chỉ cần nửa phổ vì tín hiệu thực đối xứng)
        int half = nfft / 2;
        float[] freq = new float[half];
        float[] mag = new float[half];
        float maxMag = 0f;

        for (int i = 0; i < half; ++i)
        {
            freq[i] = i * fs / nfft;
            mag[i] = Mathf.Sqrt(real[i] * real[i] + imag[i] * imag[i]);
            if (mag[i] > maxMag) maxMag = mag[i];
        }
        if (maxMag <= 0f)
        {
            Debug.LogWarning("SPARC: phổ biên độ toàn 0 (tín hiệu không đổi), không tính được.");
            return float.NaN;
        }
        for (int i = 0; i < half; ++i) mag[i] /= maxMag; // chuẩn hóa theo đỉnh lớn nhất

        // 6. Giới hạn theo tần số cắt fc
        int fcCount = 0;
        for (int i = 0; i < half; ++i) { if (freq[i] <= fc) fcCount++; else break; }
        if (fcCount < 2)
        {
            Debug.LogWarning("SPARC: tần số cắt fc quá nhỏ so với độ phân giải phổ.");
            return float.NaN;
        }

        // 7. Trong dải [0, fc], tìm đoạn liên tục có biên độ >= ampTh (băng tần "hiệu quả")
        int startIdx = -1, endIdx = -1;
        for (int i = 0; i < fcCount; ++i)
        {
            if (mag[i] >= ampTh)
            {
                if (startIdx == -1) startIdx = i;
                endIdx = i;
            }
        }
        if (startIdx == -1 || endIdx <= startIdx)
        {
            Debug.LogWarning("SPARC: không tìm được băng tần vượt ngưỡng biên độ (amp_th).");
            return float.NaN;
        }

        // 8. Tính độ dài cung (arc length) của đường phổ biên độ đã chuẩn hóa
        //    trên đoạn [startIdx, endIdx], theo trục tần số đã chuẩn hóa về [0,1]
        float fRange = freq[endIdx] - freq[startIdx];
        if (fRange <= 0f) fRange = 1e-9f; // tránh chia 0 nếu chỉ có 1 điểm

        float arcLength = 0f;
        for (int i = startIdx; i < endIdx; ++i)
        {
            float dF = (freq[i + 1] - freq[i]) / fRange; // trục tần số chuẩn hóa
            float dM = mag[i + 1] - mag[i];
            arcLength += Mathf.Sqrt(dF * dF + dM * dM);
        }

        return -arcLength;
    }

    /// <summary>Số lũy thừa của 2 nhỏ nhất >= n.</summary>
    private int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p *= 2;
        return p;
    }

    /// <summary>
    /// FFT (Cooley-Tukey, radix-2, in-place, iterative). Yêu cầu real.Length là lũy thừa của 2.
    /// </summary>
    private void FFT(float[] real, float[] imag)
    {
        int n = real.Length;
        if (n <= 1) return;

        // Bit-reversal permutation
        for (int i = 1, j = 0; i < n; ++i)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Cooley-Tukey butterfly
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            float wReal = (float)Math.Cos(ang);
            float wImag = (float)Math.Sin(ang);

            for (int i = 0; i < n; i += len)
            {
                float curReal = 1f, curImag = 0f;
                for (int k = 0; k < len / 2; ++k)
                {
                    float uReal = real[i + k];
                    float uImag = imag[i + k];
                    float vReal = real[i + k + len / 2] * curReal - imag[i + k + len / 2] * curImag;
                    float vImag = real[i + k + len / 2] * curImag + imag[i + k + len / 2] * curReal;

                    real[i + k] = uReal + vReal;
                    imag[i + k] = uImag + vImag;
                    real[i + k + len / 2] = uReal - vReal;
                    imag[i + k + len / 2] = uImag - vImag;

                    float nextReal = curReal * wReal - curImag * wImag;
                    float nextImag = curReal * wImag + curImag * wReal;
                    curReal = nextReal;
                    curImag = nextImag;
                }
            }
        }
    }
}