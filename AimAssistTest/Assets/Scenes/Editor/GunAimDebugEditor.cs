using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace Game.Editor.Aim
{
    public class GunAimDebugEditor : EditorWindow
    {
        private float _step = 0.3f; // 绘制精度
        private float _graphWidth = 720f; // 图表宽度
        private float _graphHeight = 720f; // 图表高度
        private float _logicalRange = 180; // 逻辑范围（图表的 x 轴显示范围）
        private float _axisSize = 100f; // 坐标轴刻度大小

        private const string PrefKey_Step = "GunAimDebugEditor_Step";
        private const string PrefKey_GraphWidth = "GunAimDebugEditor_GraphWidth";
        private const string PrefKey_GraphHeight = "GunAimDebugEditor_GraphHeight";
        private const string PrefKey_LogicalRange = "GunAimDebugEditor_LogicalRange";
        private const string PrefKey_AxisSize = "GunAimDebugEditor_AxisSize";
        private MonotoneCubicDynamicSizeSpline _spline;
        private GunAimRightStickAssist _assist;

        [MenuItem("Tools/AimAssist GunAimDebugEditor")]
        public static void ShowWindow()
        {
            GetWindow<GunAimDebugEditor>("GunAimDebugEditor");
        }

        private void OnEnable()
        {
            LoadData();
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        private void OnDisable()
        {
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            _spline = null;
        }

        private void OnCompilationFinished(object obj)
        {
            _spline = null;
        }

        private void Update()
        {
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.TextArea($"Frame:{Time.frameCount}");
            _step = EditorGUILayout.FloatField("Rendering accuracy:", _step);
            _graphWidth = EditorGUILayout.FloatField("Graph width:", _graphWidth);
            _graphHeight = EditorGUILayout.FloatField("Graph height:", _graphHeight);
            _logicalRange = EditorGUILayout.FloatField("Logical Range (x-axis):", _logicalRange);
            _axisSize = EditorGUILayout.FloatField("Axis scale size:", _axisSize);

            if (GUILayout.Button("Save Settings"))
            {
                SaveData();
                Debug.Log("Settings saved!");
            }
            Color oldColor = GUI.color;
            GUI.color = Color.blue;
            EditorGUILayout.LabelField($"aim assist spline");
            GUI.color = Color.yellow;
            EditorGUILayout.LabelField($"[spline] x==y");
            GUI.color = Color.green;
            EditorGUILayout.LabelField($"raw input");
            GUI.color = oldColor;

            var lastRect = GUILayoutUtility.GetLastRect();
            Rect graphRect = new Rect(10, lastRect.yMax + 10, _graphWidth, _graphHeight);
            EditorGUI.DrawRect(graphRect, Color.gray);
            DrawGraph(graphRect);
        }

        private void DrawGraph(Rect rect)
        {
            Handles.BeginGUI();

            Vector2 center = new Vector2(rect.x + rect.width / 2, rect.y + rect.height / 2);

            //轴线
            Handles.color = Color.black;
            Handles.DrawLine(new Vector3(rect.x, center.y), new Vector3(rect.xMax, center.y));
            Handles.DrawLine(new Vector3(center.x, rect.y), new Vector3(center.x, rect.yMax));
            //center刻度线
            Handles.color = Color.white;
            float unitToPixel = rect.width / (2 * _logicalRange);
            float xPos = center.x + _logicalRange * 0.5f * unitToPixel;
            Handles.DrawLine(new Vector3(xPos, center.y - _axisSize, 0), new Vector3(xPos, center.y + _axisSize, 0));
            xPos = center.x - _logicalRange * 0.5f * unitToPixel;
            Handles.DrawLine(new Vector3(xPos, center.y - _axisSize, 0), new Vector3(xPos, center.y + _axisSize, 0));

            float yPos = center.y + _logicalRange * 0.5f * unitToPixel;
            Handles.DrawLine(new Vector3(center.x - _axisSize, yPos, 0), new Vector3(center.x + _axisSize, yPos, 0));
            yPos = center.y - _logicalRange * 0.5f * unitToPixel;
            Handles.DrawLine(new Vector3(center.x - _axisSize, yPos, 0), new Vector3(center.x + _axisSize, yPos, 0));

            if (!EditorApplication.isPlaying)
            {
                return;
            }

            //begin get spline
            if (_spline == null)
            {
                PlayerMoveTest component = GameObject.FindObjectOfType<PlayerMoveTest>();
                if (component == null) return;
                _assist = component.GunAimRightStickAssist;
                _spline = _assist.DynamicSizeSpline;
            }
            //end get spline

            // draw spline
            Handles.color = Color.blue;
            Vector3 prevPoint = Vector3.zero;

            if (_spline.DynamicSize == 0)
            {
                return;
            }

            for (float x = -_logicalRange; x < _logicalRange; x += _step)
            {
                float y = _spline.Interpolate(x * Mathf.Deg2Rad);
                y = y * Mathf.Rad2Deg;
                Vector3 point = new Vector3(center.x + x * unitToPixel, center.y - y * unitToPixel, 0);
                if (x != -_logicalRange)
                {
                    Handles.DrawLine(prevPoint, point);
                }

                prevPoint = point;
            }

            List<GunAimRightStickAssist.DebugValidPoint> validPoints = _assist.DebugValidPoints;
            for (float x = -_logicalRange; x < _logicalRange; x += _step)
            {
                float y;
                if (CheckRawInputIsValid(x * Mathf.Deg2Rad, validPoints))
                {
                    Handles.color = Color.yellow;
                    y = _spline.Interpolate(x * Mathf.Deg2Rad) * Mathf.Rad2Deg;
                }
                else
                {
                    Handles.color = Color.green;
                    y = x;
                }

                Vector3 point = new Vector3(center.x + x * unitToPixel, center.y - y * unitToPixel, 0);
                if (x != -_logicalRange)
                {
                    Handles.DrawLine(prevPoint, point);
                }

                prevPoint = point;
            }

            Handles.EndGUI();
        }

        private static bool CheckRawInputIsValid(float rawInputRad, List<GunAimRightStickAssist.DebugValidPoint> points)
        {
            bool valid = true;
            if (rawInputRad < points[0].Rad || rawInputRad > points[^1].Rad)
            {
                valid = false;
            }
            else
            {
                for (int i = 0; i < points.Count - 1; i++)
                {
                    float rad = points[i].Rad;
                    float nextRad = points[i + 1].Rad;
                    if (rawInputRad > rad && rawInputRad < nextRad && points[i].IsEndBlack && points[i + 1].IsStartBlack)
                    {
                        valid = false;
                    }
                }
            }

            return valid;
        }

        private void SaveData()
        {
            // 将数据保存到 EditorPrefs
            EditorPrefs.SetFloat(PrefKey_Step, _step);
            EditorPrefs.SetFloat(PrefKey_GraphWidth, _graphWidth);
            EditorPrefs.SetFloat(PrefKey_GraphHeight, _graphHeight);
            EditorPrefs.SetFloat(PrefKey_LogicalRange, _logicalRange);
            EditorPrefs.SetFloat(PrefKey_AxisSize, _axisSize);
        }

        private void LoadData()
        {
            // 从 EditorPrefs 加载数据
            _step = EditorPrefs.GetFloat(PrefKey_Step, 0.1f);
            _graphWidth = EditorPrefs.GetFloat(PrefKey_GraphWidth, 400f);
            _graphHeight = EditorPrefs.GetFloat(PrefKey_GraphHeight, 400f);
            _logicalRange = EditorPrefs.GetFloat(PrefKey_LogicalRange, 10f);
            _axisSize = EditorPrefs.GetFloat(PrefKey_AxisSize, 5f);
        }
    }
}
