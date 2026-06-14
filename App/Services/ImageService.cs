using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace App.Services
{
    internal class ImageService
    {
        public enum ImageType : uint
        {
            IMAGE_BITMAP = 0,
            IMAGE_ICON = 1,
            IMAGE_CURSOR = 2,
        }

        [Flags]
        public enum LoadImageFlags : uint
        {
            LR_CREATEDIBSECTION = 0x00002000,
            LR_DEFAULTCOLOR = 0x0,
            LR_DEFAULTSIZE = 0x00000040,
            LR_LOADFROMFILE = 0x00000010,
            LR_LOADMAP3DCOLORS = 0x00001000,
            LR_LOADTRANSPARENT = 0x00000020,
            LR_MONOCHROME = 0x00000001,
            LR_SHARED = 0x00008000,
            LR_VGACOLOR = 0x00000080,
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern unsafe IntPtr LoadImage(
            IntPtr hInst,
            string name,
            ImageType type,
            int cx,
            int cy,
            LoadImageFlags fuLoad);
    }
}
