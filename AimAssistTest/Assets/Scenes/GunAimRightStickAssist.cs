using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

[Serializable]
public class GunAimRightStickAssist
{
    #region Struct

    [Serializable]
    private struct Context
    {
        [field: SerializeField] public float SearchRange { get; set; } //= 12f;
        [field: SerializeField] public float JoyStickTriggerThreshold { get; set; } // = 0.05f; // angle deadZone
        [field: SerializeField] public float BlackPointRadius { get; set; } // = 0.3f;
        [field: SerializeField] public float CompressionRatio { get; set; } // = 0.6f, // k y=kx
        [field: SerializeField] public float TooCloseRatio { get; set; } //= 1.6f
    }

    private struct Element
    {
        public float StartRad { get; set; }
        public float CenterRad { get; set; }
        public float EndRad { get; set; }
        public float AssistRadius { get; set; }
        public float Distance { get; set; }
        public bool TooClose { get; set; } //delete too close element
    }

    private enum PointType
    {
        Start = 0,
        Center = 1,
        End = 2,
        BlackStart = 3,
        BlackEnd = 4
    }

#if UNITY_EDITOR
    public struct DebugValidPoint
    {
        public bool IsStartBlack { get; set; }
        public bool IsEndBlack { get; set; }
        public float Rad { get; set; }
    }
#endif

    private struct Point
    {
        public int ElementID { get; set; }
        public PointType Type { get; set; }
        public float OriginalRad { get; set; }
        public float AssistRadius { get; set; }
        public float Distance { get; set; }
        public float CalculateCenterRad { get; set; } //For swap point, Calculate data for spline y
        public float CalculateRadius { get; set; }
        public PointType CalculateType { get; set; }
    }

    #endregion

    #region Field

    [SerializeField] private Context _context;
    private Vector3? _lastJoystickDirection;
    private Vector3? _lastLastJoystickDirection;
    private const int MaxPoints = 300;
    private readonly float[] _xValue = new float[MaxPoints];
    private readonly float[] _yValue = new float[MaxPoints];
    [NonSerialized] private readonly MonotoneCubicDynamicSizeSpline _dynamicSizeSpline;
    public MonotoneCubicDynamicSizeSpline DynamicSizeSpline => _dynamicSizeSpline;
#if UNITY_EDITOR
    public List<DebugValidPoint> DebugValidPoints { get; } = new();
#endif

    #endregion

    private Target[] _targets;

    private GunAimRightStickAssist()
    {
        _dynamicSizeSpline = new MonotoneCubicDynamicSizeSpline(_xValue, _yValue);
    }

    public void SetTargets(Target[] targets)
    {
        _targets = targets;
    }

    public (bool /*isValid*/, Vector3 /*direction*/) GetDirection(Vector3 aimDirection, Vector3 playerPosition)
    {
        bool isValid = TryGetTargetsAngle(
            joystickDirection: aimDirection,
            playerPosition: playerPosition,
            out Vector3 newDirection);
        return (isValid, newDirection);
    }

    public void UnsetTarget()
    {
        _lastJoystickDirection = null;
        _lastLastJoystickDirection = null;
    }

    private bool TryGetTargetsAngle(Vector3 joystickDirection, Vector3 playerPosition, out Vector3 newDirection)
    {
        if (_lastJoystickDirection == null)
        {
            _lastJoystickDirection = joystickDirection;
        }
        else if (_lastLastJoystickDirection == null)
        {
            Vector2 lastJoystickVector2 = new Vector2(_lastJoystickDirection.Value.x, _lastJoystickDirection.Value.z);
            Vector2 joystickVector2 = new Vector2(joystickDirection.x, joystickDirection.z);
            float signedJoystickTurnAngle = Vector2.SignedAngle(lastJoystickVector2, joystickVector2);

            // Debug.Log($"[aim_assist] signedJoystickTurnAngle={signedJoystickTurnAngle} lastJoystickVector2={lastJoystickVector2} joystickVector2={joystickVector2} " +
            //           $"frame={Time.frameCount}");
            _lastJoystickDirection = joystickDirection;
            _lastLastJoystickDirection = _lastJoystickDirection;
            if (Mathf.Abs(signedJoystickTurnAngle) < _context.JoyStickTriggerThreshold)
            {
                newDirection = Vector3.one; //useless
                //Debug.Log($"[aim_assist] ----------end _lastLastJoystickDirection == null frame={Time.frameCount}");
                return false;
            }
        }
        else
        {
            //Determine whether the directions of the last two frames have the same orientation.
            //If they differ, no assistance is provided.
            //This also helps mitigate hardware joystick jitter issues (even though the JoyStickTriggerThreshold partially addresses some of the hardware joystick jitter).
            Vector2 lastJoystickVector2 = new Vector2(_lastJoystickDirection.Value.x, _lastJoystickDirection.Value.z);
            Vector2 joystickVector2 = new Vector2(joystickDirection.x, joystickDirection.z);
            float signedJoystickTurnAngle = Vector2.SignedAngle(lastJoystickVector2, joystickVector2);
            float signedLastJoystickTurnAngle = Vector2.SignedAngle(new Vector2(_lastLastJoystickDirection.Value.x, _lastLastJoystickDirection.Value.z), lastJoystickVector2);

            if (Mathf.Abs(signedJoystickTurnAngle) < _context.JoyStickTriggerThreshold
                || signedLastJoystickTurnAngle * signedJoystickTurnAngle < 0)
            {
                newDirection = Vector3.one; //useless

                //Debug.Log($"[aim_assist] ----------end Mathf.Abs(signedJoystickTurnAngle) < _data.JoyStickTriggerThreshold={Mathf.Abs(signedJoystickTurnAngle) < _context.JoyStickTriggerThreshold}" +
                //         $"signedLastJoystickTurnAngle={signedLastJoystickTurnAngle} signedJoystickTurnAngle={signedJoystickTurnAngle} frame={Time.frameCount}");
                return false;
            }
        }

        float searchRange = _context.SearchRange;
        using PooledList<Element> elements = new PooledList<Element>(2);
        foreach (Target target in _targets)
        {
            Transform transform = target.transform;
            Vector3 targetPosition = transform.position;

            if (Vector3.Distance(playerPosition, targetPosition) > searchRange)
            {
                continue;
            }

            Vector3 playerToTargetDirection = (targetPosition - playerPosition).normalized;
            float assistRadius = target.AssistRadius;
            float realDistance = Vector3.Distance(playerPosition, targetPosition);
            float distance = Mathf.Max(realDistance, assistRadius);
            float centerRad = Mathf.Atan2(playerToTargetDirection.z, playerToTargetDirection.x);
            float rad = Mathf.Asin(assistRadius / distance);
            float leftEdgeRad = centerRad - rad;
            float rightEdgeRad = centerRad + rad;

            elements.Add(new Element()
            {
                StartRad = leftEdgeRad,
                CenterRad = centerRad,
                EndRad = rightEdgeRad,
                AssistRadius = assistRadius,
                Distance = distance,
                TooClose = realDistance < assistRadius * _context.TooCloseRatio
            });
        }

        //BEGIN delete all overlap elements
        //Debug.Log($"[aim_assist] original elements.Count:{elements.Count} frame:{Time.frameCount}");
        using PooledList<int> removeIndexes = new PooledList<int>(2);
        using PooledDictionary<int /*elementId*/, (float start, float end)> elementSetMap = new(2);
        for (int i = 0; i < elements.Count; i++)
        {
            Element element = elements[i];
            elementSetMap[i] = (element.StartRad, element.EndRad);
        }

        GetOverlapSetIds(elementSetMap.GetValue(), removeIndexes.GetValue());

        for (int i = removeIndexes.Count - 1; i >= 0; i--)
        {
            elements.GetValue().RemoveAtSwapBack(removeIndexes[i]);
        }

        //Debug.Log($"[aim_assist] remove complete overlap removeCount={removeIndexes.Count} elements.Count:{elements.Count} frame:{Time.frameCount}");
        //END delete all overlap elements

        //BEGIN delete all TooClose elements
        removeIndexes.Clear();
        for (int i = 0; i < elements.Count; i++)
        {
            if (elements[i].TooClose)
            {
                removeIndexes.Add(i);
            }
        }

        for (int i = removeIndexes.Count - 1; i >= 0; i--)
        {
            elements.GetValue().RemoveAtSwapBack(removeIndexes[i]);
        }

        //Debug.Log($"[aim_assist] remove tooClose overlap removeCount={removeIndexes.Count} elements.Count:{elements.Count} frame:{Time.frameCount}");
        //END delete all TooClose elements

        #region Fix: head-to-tail overlapping intervals

        using PooledList<(int /*elementId*/, bool /*forward*/)> crossedElementIds = new(2);

        float minRad = float.MaxValue;
        float maxRad = float.MinValue;
        foreach (var element in elements)
        {
            float startRad = element.StartRad;
            float endRad = element.EndRad;
            if (startRad < minRad) minRad = startRad;
            if (endRad > maxRad) maxRad = endRad;
        }

        foreach (var element in elements)
        {
            int elementId = elements.IndexOf(element);
            float fallBackRad = element.EndRad - 2 * Mathf.PI;
            float forwardRad = element.StartRad + 2 * Mathf.PI;

            if (fallBackRad > minRad && !crossedElementIds.Contains((elementId, false)))
            {
                crossedElementIds.Add((elementId, false));
            }

            if (forwardRad < maxRad && !crossedElementIds.Contains((elementId, true)))
            {
                crossedElementIds.Add((elementId, true));
            }
        }

        for (int i = 0; i < crossedElementIds.Count; i++)
        {
            (int elementId, bool forward) = crossedElementIds[i];
            float offsetRad = forward ? 2 * Mathf.PI : -2 * Mathf.PI;
            elements.Add(new Element()
            {
                StartRad = elements[elementId].StartRad + offsetRad,
                CenterRad = elements[elementId].CenterRad + offsetRad,
                EndRad = elements[elementId].EndRad + offsetRad,
                AssistRadius = elements[elementId].AssistRadius,
                Distance = elements[elementId].Distance,
                TooClose = elements[elementId].TooClose
            });

            //Debug.Log($"[aim_assist] add crossedElement: elementID={elementId} forward={forward} frame={Time.frameCount}");
        }

        //remove overlap elements again
        elementSetMap.Clear();
        for (int i = 0; i < elements.Count; i++)
        {
            Element element = elements[i];
            elementSetMap[i] = (element.StartRad, element.EndRad);
        }

        GetOverlapSetIds(elementSetMap.GetValue(), removeIndexes.GetValue());

        for (int i = removeIndexes.Count - 1; i >= 0; i--)
        {
            elements.GetValue().RemoveAtSwapBack(removeIndexes[i]);
        }

        #endregion Fix: head-to-tail overlapping intervals

        //Debug.Log($"[aim_assist] -------------------begin---[build-spline]----elements={elements.Count}-----frame={Time.frameCount}----------------------------");
        //build points firstly
        using PooledList<Point> points = new PooledList<Point>(elements.Count * 3);
        for (int i = 0; i < elements.Count; i++)
        {
            Element element = elements[i];
            points.Add(new Point()
            {
                ElementID = i,
                Type = PointType.Start,
                AssistRadius = element.AssistRadius,
                OriginalRad = element.StartRad,
                Distance = element.Distance,
                CalculateCenterRad = element.CenterRad,
                CalculateRadius = element.AssistRadius,
                CalculateType = PointType.Start
            });

            points.Add(new Point()
            {
                ElementID = i,
                Type = PointType.Center,
                AssistRadius = element.AssistRadius,
                OriginalRad = element.CenterRad,
                Distance = element.Distance,
                CalculateCenterRad = element.CenterRad,
                CalculateRadius = element.AssistRadius,
                CalculateType = PointType.Center
            });

            points.Add(new Point()
            {
                ElementID = i,
                Type = PointType.End,
                AssistRadius = element.AssistRadius,
                OriginalRad = element.EndRad,
                Distance = element.Distance,
                CalculateCenterRad = element.CenterRad,
                CalculateRadius = element.AssistRadius,
                CalculateType = PointType.End
            });
        }

        //sort points
        points.GetValue().Sort((a, b) => a.OriginalRad.CompareTo(b.OriginalRad));

        //log
        // string log = "";
        // foreach (Point point in points)
        // {
        //     log += $"ID:{point.ElementID}, Angle:{point.OriginalRad * Mathf.Rad2Deg},  Radius:{point.AssistRadius}, Type:{point.Type}\n";
        // }
        //Debug.Log($"[aim_assist] sort points:\n{log} frame={Time.frameCount}");
        using PooledDictionary<int /*elementId*/, (int /*startIndex*/, int /*centerIndex*/, int /*endIndex*/)> elementPointsMap = new(2);

        #region remove start/end points

        BuildElementPointsMap(points.GetValue(), elementPointsMap);
        using PooledList<int> removePointIndexes = new(2);
        foreach ((int targetElementId, (int targetStartIndex, int targetCenterIndex, int targetEndIndex)) in elementPointsMap)
        {
            float targetStartAngle = points[targetStartIndex].OriginalRad;
            float targetCenterAngle = points[targetCenterIndex].OriginalRad;
            float targetEndAngle = points[targetEndIndex].OriginalRad;

            foreach ((int bElementId, (int bStartIndex, int bCenterIndex, int bEndIndex)) in elementPointsMap)
            {
                if (targetElementId == bElementId) continue;

                float bStartAngle = points[bStartIndex].OriginalRad;
                float bCenterAngle = points[bCenterIndex].OriginalRad;
                float bEndAngle = points[bEndIndex].OriginalRad;

                if (bStartAngle < targetStartAngle && targetStartAngle < bCenterAngle
                    || ((bStartAngle < targetCenterAngle && targetCenterAngle < bEndAngle)
                        && (bStartAngle < targetStartAngle && targetStartAngle < bEndAngle)))
                {
                    if (!removePointIndexes.Contains(targetStartIndex))
                        removePointIndexes.Add(targetStartIndex);
                }
                else if (bCenterAngle < targetEndAngle && targetEndAngle < bEndAngle
                         || ((bStartAngle < targetCenterAngle && targetCenterAngle < bEndAngle)
                             && (bStartAngle < targetEndAngle && targetEndAngle < bEndAngle)))
                {
                    if (!removePointIndexes.Contains(targetEndIndex))
                        removePointIndexes.Add(targetEndIndex);
                }
            }
        }

        //need to sort
        removePointIndexes.GetValue().Sort((a, b) => a.CompareTo(b));
        for (int i = removePointIndexes.Count - 1; i >= 0; i--)
        {
            int index = removePointIndexes[i];
            //Debug.Log($"[ai_assist] remove point ElementID={points[index].ElementID} type={points[index].Type} frame={Time.frameCount}");
            points.RemoveAt(index);
        }

        #endregion

        #region Swap begin, some points had been removed

        BuildElementPointsMap(points.GetValue(), elementPointsMap);
        //this swap, we need to swap the endPoint and nextStartPoint
        using PooledList<(int /*end*/, int /*next_start*/)> swapPoints = new(2);
        using PooledList<int> sortElementIds = new(2);
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Type == PointType.Center)
            {
                sortElementIds.Add(points[i].ElementID);
            }
        }

        for (int i = 0; i < sortElementIds.Count - 1; i++)
        {
            (int _, int targetCenterIndex, int targetEndIndex) = elementPointsMap[sortElementIds[i]];
            (int bStartIndex, int bCenterIndex, _) = elementPointsMap[sortElementIds[i + 1]];
            if (targetEndIndex == -1 || bStartIndex == -1) continue;
            float targetCenterAngle = points[targetCenterIndex].OriginalRad;
            float targetEndAngle = points[targetEndIndex].OriginalRad;
            float bStartAngle = points[bStartIndex].OriginalRad;
            float bCenterAngle = points[bCenterIndex].OriginalRad;
            if (targetCenterAngle < bStartAngle && targetEndAngle > bStartAngle && targetEndAngle < bCenterAngle)
            {
                //Debug.Log($"[aim_assist] swap targetEndIndex={targetEndIndex} bStartIndex={bStartIndex}");
                swapPoints.Add((targetEndIndex, bStartIndex));
            }
        }

        //swap action: swap first, then remove
        foreach ((int endPointIndex, int nextStartPointIndex) in swapPoints)
        {
            var endPoint = points[endPointIndex];
            var nextStartPoint = points[nextStartPointIndex];
            //Debug.Log($"[endPoint] indexA={endPoint} nextStartPoint={nextStartPoint}");

            points[endPointIndex] = new Point()
            {
                ElementID = endPoint.ElementID,
                Type = endPoint.Type,
                OriginalRad = endPoint.OriginalRad,
                AssistRadius = endPoint.AssistRadius,
                Distance = endPoint.Distance,
                //
                CalculateCenterRad = nextStartPoint.CalculateCenterRad,
                CalculateRadius = nextStartPoint.CalculateRadius,
                CalculateType = PointType.Start,
            };
            points[nextStartPointIndex] = new Point()
            {
                ElementID = nextStartPoint.ElementID,
                Type = nextStartPoint.Type,
                OriginalRad = nextStartPoint.OriginalRad,
                AssistRadius = nextStartPoint.AssistRadius,
                Distance = nextStartPoint.Distance,
                //
                CalculateCenterRad = endPoint.CalculateCenterRad,
                CalculateRadius = endPoint.CalculateRadius,
                CalculateType = PointType.End
            };
            //Debug.Log($"[aim_assist] swap point end={endPoint.ElementID} next_start={nextStartPoint.ElementID} frame={Time.frameCount}");
        }

        #endregion swap end

        // log = "";
        // foreach (Point point in points)
        // {
        //     log += $"ID:{point.ElementID}, Angle:{point.OriginalRad * Mathf.Rad2Deg}   Radius:{point.AssistRadius}, Type:{point.Type}\n";
        // }

        //Debug.Log($"[aim_assist] before add black, points:\n{log} frame={Time.frameCount}");

        // Add black points to the points list, three ways:
        // 1. For multiple points: between endPoint and NextStartPoint, there are three cases:
        //    (a) If the distance is greater than endRadius + nextStartRadius, add two black points;
        //    (b) If the distance is greater than endRadius, add one black point;
        //    (c) If the distance is less than endRadius, do not add any black points.
        // 2. If index = 0, it is a startPoint.
        // 3. If index = points.Count - 1, it is an endPoint.
        // For now, configure the black radius as a fixed value. Thus, the smaller the angle, the smaller the transitional blackAngle: blackAngle = blackRadius / distance.

        if (points.Count > 1)
        {
            for (int i = points.Count - 1; i >= 1;)
            {
                Point nextStartPoint = points[i];
                Point endPoint = points[i - 1];
                if (nextStartPoint.ElementID != endPoint.ElementID
                    && nextStartPoint.Type == PointType.Start
                    && endPoint.Type == PointType.End)
                {
                    float offsetAngle = nextStartPoint.OriginalRad - endPoint.OriginalRad;
                    float endBlackAngle = Mathf.Asin(_context.BlackPointRadius / endPoint.Distance);
                    float nextStartBlackAngle = Mathf.Asin(_context.BlackPointRadius / nextStartPoint.Distance);

                    if (offsetAngle > (endBlackAngle + nextStartBlackAngle))
                    {
                        float leftEndAngle = Mathf.Min(nextStartPoint.OriginalRad - nextStartBlackAngle, endPoint.OriginalRad + endBlackAngle);
                        float rightStartAngle = Mathf.Max(nextStartPoint.OriginalRad - nextStartBlackAngle, endPoint.OriginalRad + endBlackAngle);
                        points.Insert(i, new Point()
                        {
                            ElementID = nextStartPoint.ElementID,
                            Type = PointType.BlackStart,
                            OriginalRad = rightStartAngle,
                            AssistRadius = nextStartPoint.AssistRadius,
                            Distance = nextStartPoint.Distance,
                            CalculateCenterRad = nextStartPoint.CalculateCenterRad,
                            CalculateRadius = nextStartPoint.CalculateRadius,
                            CalculateType = nextStartPoint.CalculateType
                        });

                        points.Insert(i, new Point()
                        {
                            ElementID = endPoint.ElementID,
                            Type = PointType.BlackEnd,
                            OriginalRad = leftEndAngle,
                            AssistRadius = endPoint.AssistRadius,
                            Distance = endPoint.Distance,
                            CalculateCenterRad = endPoint.CalculateCenterRad,
                            CalculateRadius = endPoint.CalculateRadius,
                            CalculateType = endPoint.CalculateType
                        });
                        //Debug.Log($"[aim_assist]insert two black: end and nextStart point endPointId={endPoint.ElementID} nextStartPoint.OriginalAngle={nextStartPoint.OriginalRad * Mathf.Rad2Deg} endBlackAngle={endBlackAngle * Mathf.Rad2Deg} nextStartPointId={nextStartPoint.ElementID} nextStartBlackAngle={nextStartBlackAngle * Mathf.Rad2Deg} frame={Time.frameCount}");
                    }
                    else if (offsetAngle > endBlackAngle)
                    {
                        points.Insert(i, new Point()
                        {
                            ElementID = endPoint.ElementID,
                            Type = PointType.BlackEnd,
                            OriginalRad = endPoint.OriginalRad + endBlackAngle,
                            AssistRadius = endPoint.AssistRadius,
                            Distance = endPoint.Distance,
                            CalculateCenterRad = endPoint.CalculateCenterRad,
                            CalculateRadius = endPoint.CalculateRadius,
                            CalculateType = endPoint.CalculateType
                        });
                        //Debug.Log($"[aim_assist] insert one black: point endPointId={endPoint.ElementID} nextStartPointId={nextStartPoint.ElementID} endPoint.OriginalAngle={endPoint.OriginalRad * Mathf.Rad2Deg} endBlackAngle={endBlackAngle * Mathf.Rad2Deg} frame={Time.frameCount}");
                    }

                    i -= 2;
                }
                else
                {
                    i--;
                }
            }

            //insert black for first and last point
            Point firstPoint = points[0];
            float blackAngle = firstPoint.OriginalRad - Mathf.Asin(_context.BlackPointRadius / firstPoint.Distance);

            points.Insert(0, new Point()
            {
                ElementID = firstPoint.ElementID,
                Type = PointType.BlackStart,
                OriginalRad = blackAngle,
                AssistRadius = firstPoint.AssistRadius,
                Distance = firstPoint.Distance,
                CalculateCenterRad = firstPoint.CalculateCenterRad,
                CalculateRadius = firstPoint.CalculateRadius,
                CalculateType = firstPoint.CalculateType
            });
            // points.Insert(0, firstPoint with { Type = PointType.BlackStart, OriginalRad = blackAngle });
            Point lastPoint = points[^1];
            blackAngle = lastPoint.OriginalRad + Mathf.Asin(_context.BlackPointRadius / lastPoint.Distance);
            points.Add(new Point()
            {
                ElementID = lastPoint.ElementID,
                Type = PointType.BlackEnd,
                OriginalRad = blackAngle,
                AssistRadius = lastPoint.AssistRadius,
                Distance = lastPoint.Distance,
                CalculateCenterRad = lastPoint.CalculateCenterRad,
                CalculateRadius = lastPoint.CalculateRadius,
                CalculateType = lastPoint.CalculateType
            });
        }

        // log = "";
        // foreach (Point point in points)
        // {
        //     log += $"ID:{point.ElementID}, Angle:{point.OriginalRad * Mathf.Rad2Deg}, Radius:{point.AssistRadius}, Type:{point.Type}\n";
        // }
        //
        // Debug.Log($"[aim_assist] after add black, points:\n{log} frame={Time.frameCount}");
        float rawInputRad = Mathf.Atan2(joystickDirection.z, joystickDirection.x);

        if (points.Count < 2)
        {
            //Debug.Log($"[aim_assist] ----------end points.Count < 2 frame={Time.frameCount}");
            newDirection = Vector3.one;
            return false;
        }

        #region Check rawInputRad is in the range of the splineX

#if UNITY_EDITOR
        DebugValidPoints.Clear();
        for (int i = 0; i < points.Count; i++)
        {
            DebugValidPoints.Add(new DebugValidPoint()
                {
                    IsStartBlack = points[i].Type == PointType.BlackStart,
                    IsEndBlack = points[i].Type == PointType.BlackEnd,
                    Rad = points[i].OriginalRad
                }
            );
        }
#endif
        if (!CheckRawInputIsValid(rawInputRad, points.GetValue()))
        {
            //Debug.Log($"[aim_assist] ----------end  CheckRawInputIsValid=false frame={Time.frameCount}");
            newDirection = Vector3.one;
            return false;
        }

        #endregion

        //build spline
        Debug.Assert(points.Count <= MaxPoints);
        int pointCount = points.Count;
        for (int i = 0; i < pointCount; i++)
        {
            Point point = points[i];
            _xValue[i] = point.OriginalRad;

            if (point.CalculateType == PointType.Start)
            {
                _yValue[i] = point.CalculateCenterRad - Mathf.Abs(point.CalculateCenterRad - point.OriginalRad) * _context.CompressionRatio;
            }
            else if (point.CalculateType == PointType.End)
            {
                _yValue[i] = point.CalculateCenterRad + Mathf.Abs(point.CalculateCenterRad - point.OriginalRad) * _context.CompressionRatio;
            }
            else
            {
                _yValue[i] = point.OriginalRad;
            }
        }

        // //begin debug
        // log = "";
        // for (int i = 0; i < pointCount; i++)
        // {
        //     float x = _xValue[i];
        //     float y = _yValue[i];
        //     log += $"x={x * Mathf.Rad2Deg} y={y * Mathf.Rad2Deg}\n";
        // }
        //
        // Debug.Log($"[aim_assist] check spline x y  \n{log} frame={Time.frameCount}");

        float? lastY = null;
        for (int i = 0; i < pointCount; i++)
        {
            float y = _yValue[i];
            if (lastY != null && lastY.Value > y)
            {
                Debug.Assert(false, $"[aim_assist] spline y is not monotonic frame={Time.frameCount}");
            }

            lastY = y;
        }
        //end debug

        _dynamicSizeSpline.CalculateSpline(pointCount);
        float resultRad = _dynamicSizeSpline.Interpolate(rawInputRad);
        newDirection = new Vector3(Mathf.Cos(resultRad), 0, Mathf.Sin(resultRad));

        // Debug.Log($"[aim_assist] ----------end--build-spline-----rawInputRad={rawInputRad * Mathf.Rad2Deg}----" +
        //           $"---resultRad={resultRad * Mathf.Rad2Deg}----------time={Time.frameCount}-----------------");
        return true;
    }

    private static void BuildElementPointsMap(IReadOnlyList<Point> points, Dictionary<int, (int, int, int)> elementPointsMap)
    {
        elementPointsMap.Clear();
        for (int i = 0; i < points.Count; i++)
        {
            int elementID = points[i].ElementID;
            PointType type = points[i].Type;

            if (!elementPointsMap.TryGetValue(elementID, out var indices))
            {
                indices = (-1, -1, -1);
            }

            elementPointsMap[elementID] = type switch
            {
                PointType.Start => (i, indices.Item2, indices.Item3),
                PointType.Center => (indices.Item1, i, indices.Item3),
                PointType.End => (indices.Item1, indices.Item2, i),
                _ => indices
            };
        }
    }

    /// <summary>
    /// get overlap set id list
    /// </summary>
    private static void GetOverlapSetIds(IReadOnlyDictionary<int, (float startValue, float endValue)> setMap, List<int> overlapResults)
    {
        overlapResults.Clear();
        foreach ((int setId, (float startValue, float endValue)) in setMap)
        {
            float start = startValue;
            float end = endValue;
            foreach ((int otherSetId, (float otherStartValue, float otherEndValue)) in setMap)
            {
                if (setId == otherSetId) continue;

                if (start >= otherStartValue && end <= otherEndValue)
                {
                    start = end = -1;
                    break;
                }

                if (start >= otherStartValue && start <= otherEndValue)
                {
                    start = otherEndValue;
                }

                if (end >= otherStartValue && end <= otherEndValue)
                {
                    end = otherStartValue;
                }
            }

            if (start >= end)
            {
                overlapResults.Add(setId);
            }
        }
    }

    /// <summary>
    ///  two case to invalid:
    ///1. rawInputRad out of range of splineX
    ///2. rawInputRad in the range of two black points
    /// </summary>
    private static bool CheckRawInputIsValid(float rawInputRad, IReadOnlyList<Point> points)
    {
        bool valid = true;
        if (rawInputRad < points[0].OriginalRad || rawInputRad > points[^1].OriginalRad)
        {
            valid = false;
        }
        else
        {
            for (int i = 0; i < points.Count - 1; i++)
            {
                float rad = points[i].OriginalRad;
                float nextRad = points[i + 1].OriginalRad;
                if (rawInputRad > rad && rawInputRad < nextRad && points[i].Type == PointType.BlackEnd && points[i + 1].Type == PointType.BlackStart)
                {
                    valid = false;
                }
            }
        }

        return valid;
    }
}
