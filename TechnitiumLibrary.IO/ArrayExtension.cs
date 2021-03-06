﻿/*
Technitium Library
Copyright (C) 2020  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Security.Cryptography;

namespace TechnitiumLibrary.IO
{
    public static class ArrayExtension
    {
        readonly static RandomNumberGenerator _rnd = new RNGCryptoServiceProvider();

        public static void Shuffle<T>(this T[] array)
        {
            byte[] buffer = new byte[4];

            int n = array.Length;
            while (n > 1)
            {
                _rnd.GetBytes(buffer);
                int k = (int)(BitConverter.ToUInt32(buffer, 0) % n--);
                T temp = array[n];
                array[n] = array[k];
                array[k] = temp;
            }
        }
    }
}
