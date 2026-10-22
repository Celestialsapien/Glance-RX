// Glance-RX - a high-performance PDF reader
// Copyright (C) 2026 Adrian
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.
// SPDX-License-Identifier: AGPL-3.0

using System;

namespace GlanceRX.Services;

/// 一维【临界阻尼弹簧】(critically damped spring)，用解析解积分。
public struct Spring1D
{
    public double Value;

    /// <summary>当前速度（单位/秒）。</summary>
    public double Velocity;

    public double Target;

    public void Reset(double v)
    {
        Value = v;
        Target = v;
        Velocity = 0;
    }

    /// <summary>吸附到目标并清零速度（收敛判定通过后调用，避免尾部无限逼近）。</summary>
    public void SnapToTarget()
    {
        Value = Target;
        Velocity = 0;
    }

    /// <summary>推进 dt 秒。omega = 角频率(rad/s)，越大越"硬"（跟手、收敛快），越小越"软"（顺滑、跟随迟）。 经验换算：99% 收敛时间 ≈ 6.6 / omega 秒。</summary>
    public void Step(double dt, double omega)
    {
        if (dt <= 0) return;
        if (omega <= 0) { SnapToTarget(); return; }

        double d = Value - Target;
        double c = Velocity + omega * d;
        double e = Math.Exp(-omega * dt);

        Value = Target + (d + c * dt) * e;
        Velocity = (Velocity - c * omega * dt) * e;
    }

    public readonly bool IsSettled(double positionEpsilon, double velocityEpsilon)
        => Math.Abs(Target - Value) <= positionEpsilon && Math.Abs(Velocity) <= velocityEpsilon;
}
