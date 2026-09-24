using System;
using System.Drawing;

namespace Lugworm.Ui
{
    /// <summary>Provides the Lugworm component icons embedded in the assembly resources.</summary>
    public static class IamIcons
    {

        public static Bitmap Slicer() => Load("IamSlicer_24.png");
        public static Bitmap Mover() => Load("IamMover_24.png");
        public static Bitmap Gcode() => Load("IamGcode_24.png");
        public static Bitmap GcodeSave() => Load("IamGcodeSave_24.png");
        public static Bitmap GcodeRestart() => Load("IamGcodeRestart_24.png");
        public static Bitmap Robot() => Load("IamRobot_24.png");
        public static Bitmap RobotDefinition() => Load("IamRobotDefinition_24.png");
        public static Bitmap PrintBed() => Load("IamPrintBed_24.png");
        public static Bitmap Overhang() => Load("IamOverhang_24.png");
        public static Bitmap DoeDesign() => Load("IamDoeDesign_24.png");
        public static Bitmap Rsm() => Load("IamRsm_24.png");
        public static Bitmap Desirability() => Load("IamDesirability_24.png");
        public static Bitmap ParetoSelect() => Load("IamParetoSelect_24.png");
        public static Bitmap TestGeo() => Load("IamTestGeo_24.png");
        public static Bitmap Stats() => Load("IamStats_24.png");
        public static Bitmap Interlayer() => Load("IamInterlayer_24.png");
        public static Bitmap Legend() => Load("IamLegend_24.png");
        public static Bitmap Settings() => Load("IamSettings_24.png");
        public static Bitmap DuetConnect() => Load("IamDuetConnect_24.png");
        public static Bitmap DuetStatus() => Load("IamDuetStatus_24.png");
        public static Bitmap DuetLive() => Load("IamDuetLive_24.png");
        public static Bitmap DuetJog() => Load("IamDuetJog_24.png");
        public static Bitmap DuetTerminal() => Load("IamDuetTerminal_24.png");
        public static Bitmap DuetPrint() => Load("IamDuetPrint_24.png");
        public static Bitmap DuetFiles() => Load("IamDuetFiles_24.png");

        private static Bitmap Load(string fileName)
        {
            var resourceName = "Lugworm.Resources." + fileName;
            using var resourceBitmap = IconMaker.FromResource(fileName);
            if (resourceBitmap == null)
                throw new InvalidOperationException("Embedded icon resource was not found: " + resourceName);

            return new Bitmap(resourceBitmap);
        }
    }
}
