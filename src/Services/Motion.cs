// Glance - a high-performance PDF reader
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

namespace Glance.Services;

/// <summary>
/// 一维【临界阻尼弹簧】(critically damped spring)，用解析解积分。
///
/// 为什么用它替代原来的 lerp / 定时长缓动：
///   - lerp(每帧按固定比例靠近目标)：目标一变，速度立刻从 0 跳到最大再指数衰减。
///     连续输入(密集滚轮)时速度曲线是一串锯齿 → 眼睛看到的就是"一顿一顿"。
///   - 定时长缓动(smoothstep 220ms)：中途重新触发必须重启时钟，速度在接缝处跳变；
///     且时长固定，小位移显得拖沓、大位移显得仓促。
///   - 临界阻尼弹簧：位置 C0、速度 C1 连续；新目标只改终点、不打断当前速度，
///     所以连续输入合成的是一条光滑曲线。阻尼比恰为 1 → 最快收敛且【绝不过冲、绝不回弹】，
///     这对文档阅读很重要（过冲会让页面在停下时来回晃）。
///
/// 解析解（ζ=1 的二阶系统，对任意 dt 都精确，不会像欧拉积分那样在长帧时发散）：
///   设 d = x - target, c = v + ω·d
///   x(t+dt) = target + (d + c·dt)·e^(-ω·dt)
///   v(t+dt) = (v - c·ω·dt)·e^(-ω·dt)
/// </summary>
public struct Spring1D
{
    /// <summary>当前值（显示位置）。</summary>
    public double Value;

    /// <summary>当前速度（单位/秒）。</summary>
    public double Velocity;

    /// <summary>目标值。</summary>
    public double Target;

    /// <summary>把值与目标一起置为 v，并清零速度（开始一段全新手势时用）。</summary>
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

    /// <summary>
    /// 推进 dt 秒。omega = 角频率(rad/s)，越大越"硬"（跟手、收敛快），越小越"软"（顺滑、跟随迟）。
    /// 经验换算：99% 收敛时间 ≈ 6.6 / omega 秒。
    /// </summary>
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

    /// <summary>是否已收敛（剩余距离与速度同时进入阈值）。</summary>
    public readonly bool IsSettled(double positionEpsilon, double velocityEpsilon)
        => Math.Abs(Target - Value) <= positionEpsilon && Math.Abs(Velocity) <= velocityEpsilon;
}
