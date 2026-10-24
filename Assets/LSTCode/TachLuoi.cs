using System.Collections.Generic;
using UnityEngine;

public class TachLuoi : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created

    public Material material;
    void Start()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        Mesh mesh = mf.mesh;

        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 v0 = vertices[triangles[i]];
            Vector3 v1 = vertices[triangles[i + 1]];
            Vector3 v2 = vertices[triangles[i + 2]];

            GameObject triObj = new GameObject("Tri_" + (i / 3));
            triObj.transform.SetParent(transform, false);

            // ===== TẠO BASIS =====
            Vector3 xAxis = (v1 - v0).normalized;
            Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
            Vector3 yAxis = Vector3.Cross(normal, xAxis);

            if (normal == Vector3.zero)
                normal = Vector3.forward;

            Quaternion rot = Quaternion.LookRotation(normal, yAxis);

            // ❗ KHÔNG rotate object
            // triObj.transform.rotation = rot;

            // ===== ROTATE VERTEX NGƯỢC LẠI =====
            Vector3 lv0 = Quaternion.Inverse(rot) * v0;
            Vector3 lv1 = Quaternion.Inverse(rot) * v1;
            Vector3 lv2 = Quaternion.Inverse(rot) * v2;

            // ===== TÍNH BOUNDS =====
            Vector3 min = Vector3.Min(lv0, Vector3.Min(lv1, lv2));
            Vector3 max = Vector3.Max(lv0, Vector3.Max(lv1, lv2));

            Vector3 size = max - min;
            Vector3 center = (min + max) / 2f;

            // thêm độ dày nhỏ
            size.z = Mathf.Max(size.z, 0.002f);

            // ===== TẠO MESH =====
            Mesh triMesh = new Mesh();
            triMesh.vertices = new Vector3[] { lv0, lv1, lv2 };
            triMesh.triangles = new int[] { 0, 1, 2 };

            triObj.AddComponent<MeshFilter>().mesh = triMesh;
            triObj.AddComponent<MeshRenderer>().material = material;

            // ===== BOX =====
            BoxCollider box = triObj.AddComponent<BoxCollider>();
            box.center = center;
            box.size = size;
        }

        GetComponent<MeshRenderer>().enabled = false;
    }
}