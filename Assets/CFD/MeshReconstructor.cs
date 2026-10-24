using UnityEngine;
using System.Collections.Generic;

public class MeshReconstructor : MonoBehaviour
{
    [SerializeField] private int sliceCount = 500;

    private Mesh _sourceMesh;
    private Transform _sourceTransform;
    private MeshCollider _reconstructedCollider;

    public MeshCollider ReconstructedCollider => _reconstructedCollider;

    void Start()
    {
        _sourceMesh = GetComponent<MeshFilter>().mesh;
        _sourceTransform = transform;
        Reconstruct();
    }

    void Reconstruct()
    {
        Bounds bounds = _sourceMesh.bounds;
        float yMin = _sourceTransform.TransformPoint(bounds.min).y;
        float yMax = _sourceTransform.TransformPoint(bounds.max).y;
        float step = (yMax - yMin) / sliceCount;

        List<Vector3> allVerts = new List<Vector3>();
        List<int> allTris = new List<int>();

        for (int s = 0; s < sliceCount; s++)
        {
            float yBottom = yMin + s * step;
            float yTop = yMin + (s + 1) * step;
            float yMid = (yBottom + yTop) / 2f;

            List<List<Vector3>> contours = GetContoursAtY(yMid);

            foreach (var contour in contours)
            {
                if (contour.Count < 3) continue;
                BuildSliceCaps(contour, yBottom, yTop, allVerts, allTris);
                BuildSliceWalls(contour, yBottom, yTop, allVerts, allTris);
            }
        }

        GameObject reconstructed = new GameObject("Reconstructed_Airway");
        reconstructed.transform.position = Vector3.zero;

        MeshFilter mf = reconstructed.AddComponent<MeshFilter>();
        MeshRenderer mr = reconstructed.AddComponent<MeshRenderer>();
        mr.material = GetComponent<MeshRenderer>().material;

        Mesh newMesh = new Mesh();
        newMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        newMesh.vertices = allVerts.ToArray();
        newMesh.triangles = allTris.ToArray();
        newMesh.RecalculateNormals();
        newMesh.RecalculateBounds();
        mf.mesh = newMesh;

        _reconstructedCollider = reconstructed.AddComponent<MeshCollider>();
        _reconstructedCollider.sharedMesh = newMesh;

        Debug.Log($"Reconstructed: {allVerts.Count} verts, {allTris.Count / 3} tris");
    }

    List<List<Vector3>> GetContoursAtY(float y)
    {
        Vector3[] vertices = _sourceMesh.vertices;
        int[] triangles = _sourceMesh.triangles;

        List<Vector2[]> segments = new List<Vector2[]>();

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 v0 = _sourceTransform.TransformPoint(vertices[triangles[i]]);
            Vector3 v1 = _sourceTransform.TransformPoint(vertices[triangles[i + 1]]);
            Vector3 v2 = _sourceTransform.TransformPoint(vertices[triangles[i + 2]]);

            List<Vector2> pts = new List<Vector2>();
            TryIntersect(v0, v1, y, pts);
            TryIntersect(v1, v2, y, pts);
            TryIntersect(v2, v0, y, pts);

            if (pts.Count == 2)
                segments.Add(new Vector2[] { pts[0], pts[1] });
        }

        return ChainSegments(segments, y);
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

    List<List<Vector3>> ChainSegments(List<Vector2[]> segments, float y)
    {
        List<List<Vector3>> contours = new List<List<Vector3>>();
        bool[] used = new bool[segments.Count];

        for (int i = 0; i < segments.Count; i++)
        {
            if (used[i]) continue;

            List<Vector3> contour = new List<Vector3>();
            contour.Add(new Vector3(segments[i][0].x, y, segments[i][0].y));
            contour.Add(new Vector3(segments[i][1].x, y, segments[i][1].y));
            used[i] = true;

            bool found = true;
            while (found)
            {
                found = false;
                Vector3 last = contour[contour.Count - 1];

                for (int j = 0; j < segments.Count; j++)
                {
                    if (used[j]) continue;

                    Vector3 a = new Vector3(segments[j][0].x, y, segments[j][0].y);
                    Vector3 b = new Vector3(segments[j][1].x, y, segments[j][1].y);

                    if (Vector3.Distance(last, a) < 0.01f)
                    {
                        contour.Add(b);
                        used[j] = true;
                        found = true;
                        break;
                    }
                    else if (Vector3.Distance(last, b) < 0.01f)
                    {
                        contour.Add(a);
                        used[j] = true;
                        found = true;
                        break;
                    }
                }
            }

            if (contour.Count >= 3)
                contours.Add(contour);
        }

        return contours;
    }

    void BuildSliceCaps(List<Vector3> contour, float yBottom, float yTop,
                        List<Vector3> verts, List<int> tris)
    {
        Vector3 center = Vector3.zero;
        foreach (var p in contour) center += p;
        center /= contour.Count;

        int baseIdx = verts.Count;
        Vector3 centerBottom = new Vector3(center.x, yBottom, center.z);
        verts.Add(centerBottom);
        foreach (var p in contour)
            verts.Add(new Vector3(p.x, yBottom, p.z));

        for (int i = 0; i < contour.Count; i++)
        {
            tris.Add(baseIdx);
            tris.Add(baseIdx + 1 + (i + 1) % contour.Count);
            tris.Add(baseIdx + 1 + i);
        }

        baseIdx = verts.Count;
        Vector3 centerTop = new Vector3(center.x, yTop, center.z);
        verts.Add(centerTop);
        foreach (var p in contour)
            verts.Add(new Vector3(p.x, yTop, p.z));

        for (int i = 0; i < contour.Count; i++)
        {
            tris.Add(baseIdx);
            tris.Add(baseIdx + 1 + i);
            tris.Add(baseIdx + 1 + (i + 1) % contour.Count);
        }
    }

    void BuildSliceWalls(List<Vector3> contour, float yBottom, float yTop,
                         List<Vector3> verts, List<int> tris)
    {
        int baseIdx = verts.Count;
        int n = contour.Count;

        foreach (var p in contour)
            verts.Add(new Vector3(p.x, yBottom, p.z));
        foreach (var p in contour)
            verts.Add(new Vector3(p.x, yTop, p.z));

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int b0 = baseIdx + i;
            int b1 = baseIdx + next;
            int t0 = baseIdx + n + i;
            int t1 = baseIdx + n + next;

            tris.Add(b0); tris.Add(t0); tris.Add(b1);
            tris.Add(b1); tris.Add(t0); tris.Add(t1);
        }
    }
}