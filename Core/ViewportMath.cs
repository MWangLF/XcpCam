using System;

namespace XcpCam.Core;

public static class ViewportMath
{
    public static void Wheel(ref double left, ref double span, ref double bottom, ref double height,
        double wheel, bool control, bool alt, double xFraction, double yFraction)
    {
        double factor = Math.Pow(1.2, -wheel);
        if(control && alt)
        {
            double at = bottom + yFraction * height;
            height = Math.Clamp(height * factor, 1e-6, 1e12); bottom = at - yFraction * height;
        }
        else if(control)
        {
            double at = left + xFraction * span;
            span = Math.Clamp(span * factor, 2, 1e7); left = at - xFraction * span;
        }
        else if(alt) bottom += wheel * height * .1;
        else left -= wheel * span * .1;
    }
}
