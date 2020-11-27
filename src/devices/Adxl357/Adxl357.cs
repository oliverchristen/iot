﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Device.I2c;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using UnitsNet;

namespace Iot.Device.Adxl357
{
    /// <summary>
    /// I2C Accelerometer ADXL357
    /// </summary>
    public class Adxl357 : IDisposable
    {
        /// <summary>
        /// The default I2C address of ADXL357 device
        /// </summary>
        public const byte DefaultI2CAddress = 0x1d;

        private const int CalibrationBufferLength = 15;
        private const int CalibrationInterval = 250;

        private readonly Dictionary<string, float> _caliData = new Dictionary<string, float> { { "x", 0 }, { "y", 0 }, { "z", 0 } };
        private readonly Dictionary<string, float[]> _caliBuffer = new Dictionary<string, float[]>();
        private float _factory = 1;
        private I2cDevice _i2CDevice;

        /// <summary>
        /// Constructs a ADXL357 I2C device.
        /// </summary>
        /// <param name="i2CDevice">The I2C device used for communication.</param>
        /// <param name="accelerometerRange">The sensitivity of the accelerometer .</param>
        public Adxl357(I2cDevice i2CDevice, AccelerometerRange accelerometerRange = AccelerometerRange.Range10G)
        {
            _i2CDevice = i2CDevice ?? throw new ArgumentNullException(nameof(i2CDevice));
            Reset();

            SetAccelerometerRange(accelerometerRange);
            PowerOn();
        }

        /// <summary>
        /// Gets the current acceleration in g.
        /// Range depends on the <see cref="AccelerometerRange"/> passed to the constructor.
        /// </summary>
        public Vector3 Acceleration => GetRawAccelerometer();

        /// <summary>
        /// Gets the current temperature in °C.
        /// Range is from −40°C to +125°C.
        /// </summary>
        public Temperature Temperature => Temperature.FromDegreesCelsius(GetTemperature());

        /// <summary>
        /// Calibrates the accelerometer.
        /// You can override default <paramref name="calibrationBufferLength"/> and <paramref name="calibrationInterval"/> if required.
        /// </summary>
        /// <param name="calibrationBufferLength">The number of times every axis is measured. The average of these measurements is used to calibrate each axis.</param>
        /// <param name="calibrationInterval">The time in milliseconds to wait between each measurement.</param>
        /// <remarks>
        /// Make sure that the sensor is placed horizontally when executing this method.
        /// </remarks>
        public async Task CalibrateAccelerationSensor(int calibrationBufferLength = CalibrationBufferLength, int calibrationInterval = CalibrationInterval)
        {
            _caliBuffer["x"] = new float[calibrationBufferLength];
            _caliBuffer["y"] = new float[calibrationBufferLength];
            _caliBuffer["z"] = new float[calibrationBufferLength];

            for (int i = 0; i < calibrationBufferLength; i++)
            {
                var acc = GetRawAccelerometer();
                _caliBuffer["x"][i] = acc.X;
                _caliBuffer["y"][i] = acc.Y;
                _caliBuffer["z"][i] = acc.Z;

                await Task.Delay(calibrationInterval);
            }

            foreach (var buffer in _caliBuffer)
            {
                _caliData[buffer.Key] = buffer.Value.Average();

                if (buffer.Key == "z")
                {
                    _caliData[buffer.Key] -= 10;
                }
            }

            var x = (((_caliData["z"] - _caliData["x"]) + (_caliData["z"] - _caliData["y"])) / 2);
            _factory = 1.0F / x;
        }

        private void SetAccelerometerRange(AccelerometerRange accelerometerRange)
        {
            var currentValue = ReadByte(Register.SET_RANGE_REG_ADDR);
            var newValue = currentValue | (byte)accelerometerRange;
            WriteRegister(Register.SET_RANGE_REG_ADDR, (byte)newValue);
        }

        private double GetTemperature()
        {
            Span<byte> data = stackalloc byte[2] { 0, 0 };

            ReadBytes(Register.TEMPERATURE_REG_ADDR, data);

            double value = ((uint)data[0] << 8) | data[1];

            return 25 + (value - 1852) / -9.05;
        }

        private void Reset()
        {
            WriteRegister(Register.RESET_REG_ADDR, 0x52);
            Thread.Sleep(100);
        }

        private void PowerOn()
        {
            WriteRegister(Register.POWER_CTR_REG_ADDR, 0x00);
            Thread.Sleep(100);
        }

        private Vector3 GetRawAccelerometer()
        {
            Span<byte> data = stackalloc byte[9] { 0, 0, 0, 0, 0, 0, 0, 0, 0 };

            var ace = new Vector3();

            if (CheckDataReady())
            {
                ReadBytes(Register.FIFO_DATA_REG_ADDR, data);

                double x = ((uint)data[0] << 12) | ((uint)data[1] << 4) | ((uint)data[2] >> 4);
                double y = ((uint)data[3] << 12) | ((uint)data[4] << 4) | ((uint)data[5] >> 4);
                double z = ((uint)data[6] << 12) | ((uint)data[7] << 4) | ((uint)data[8] >> 4);

                if (((uint)x & 0x80000) == 0x80000)
                {
                    x = (double)((uint)x & 0x7ffff) - 0x80000;
                }

                if (((uint)y & 0x80000) == 0x80000)
                {
                    y = (double)((uint)y & 0x7ffff) - 0x80000;
                }

                if (((uint)z & 0x80000) == 0x80000)
                {
                    z = (double)((uint)z & 0x7ffff) - 0x80000;
                }

                ace.X = (float)x * _factory;
                ace.Y = (float)y * _factory;
                ace.Z = (float)z * _factory;
            }

            return ace;
        }

        private bool CheckDataReady()
        {
            var status = GetAdxl357Status();
            return (status & 0x01) > 0;
        }

        private byte GetAdxl357Status()
        {
            return ReadByte(Register.STATUS_REG_ADDR);
        }

        private void WriteRegister(Register register, byte value)
        {
            WriteRegister((byte)register, value);
        }

        private byte ReadByte(Register register)
        {
            return ReadByte((byte)register);
        }

        private void ReadBytes(Register register, in Span<byte> readBytes)
        {
            ReadBytes((byte)register, readBytes);
        }

        internal void WriteRegister(byte reg, byte data)
        {
            Span<byte> dataout = stackalloc byte[]
            {
                reg, data
            };

            _i2CDevice.Write(dataout);
        }

        internal byte ReadByte(byte register)
        {
            _i2CDevice.WriteByte(register);
            return _i2CDevice.ReadByte();
        }

        internal void ReadBytes(byte register, Span<byte> readBytes)
        {
            _i2CDevice.WriteByte(register);
            _i2CDevice.Read(readBytes);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _i2CDevice.Dispose();
            _i2CDevice = null!;
        }
    }
}
