import argparse
import json
import math
import random
import threading
import time
from datetime import datetime, timezone

import requests


class ElectrolyticCell:
    def __init__(self, cell_id):
        self.cell_id = cell_id
        self.voltage_base = 4.05 + random.gauss(0, 0.01)
        self.voltage = self.voltage_base
        self.anode_currents = [15.0 + random.gauss(0, 0.3) for _ in range(24)]
        self.cell_temperature = random.uniform(960, 970)
        self.bath_temperature = random.uniform(955, 965)
        self.aluminum_level = random.uniform(20, 25)
        self.bath_level = random.uniform(18, 22)
        self.alumina_concentration = random.uniform(2.5, 4.0)
        self.alumina_depletion_rate = 0.05 / 3600 + random.gauss(0, 0.005 / 3600)
        self.temperature_drift = random.gauss(0, 0.01)
        self.in_anode_effect = False
        self.anode_effect_remaining = 0
        self.anode_effect_cooldown = random.uniform(3600, 72000)
        self.anode_effect_timer = random.uniform(0, self.anode_effect_cooldown)

    def update(self, dt):
        self.alumina_concentration -= self.alumina_depletion_rate * dt
        self.alumina_concentration += random.gauss(0, 0.005) * math.sqrt(dt / 15)
        self.alumina_concentration = max(0.5, min(6.0, self.alumina_concentration))

        if self.alumina_concentration < 1.8:
            self.alumina_concentration += 0.4 + random.gauss(0, 0.05)

        self.temperature_drift += random.gauss(0, 0.001) * math.sqrt(dt / 15)
        self.temperature_drift *= 0.999
        self.cell_temperature += self.temperature_drift * dt / 15
        self.cell_temperature += random.gauss(0, 0.05) * math.sqrt(dt / 15)
        self.cell_temperature = max(950, min(985, self.cell_temperature))

        self.bath_temperature = self.cell_temperature - random.uniform(3, 8) + random.gauss(0, 0.1)

        self.aluminum_level += random.gauss(0, 0.01) * math.sqrt(dt / 15)
        self.aluminum_level += 0.0003 * dt / 15
        self.aluminum_level = max(15, min(30, self.aluminum_level))

        self.bath_level += random.gauss(0, 0.01) * math.sqrt(dt / 15)
        self.bath_level = max(14, min(26, self.bath_level))

        for i in range(24):
            self.anode_currents[i] += random.gauss(0, 0.05) * math.sqrt(dt / 15)
            self.anode_currents[i] = max(12.0, min(18.0, self.anode_currents[i]))

        self.anode_effect_timer += dt
        if not self.in_anode_effect:
            low_alumina_factor = 1.0
            if self.alumina_concentration < 2.0:
                low_alumina_factor = 3.0 + (2.0 - self.alumina_concentration) * 5.0
            if self.anode_effect_timer > self.anode_effect_cooldown / low_alumina_factor:
                if random.random() < 0.01 * low_alumina_factor:
                    self.in_anode_effect = True
                    self.anode_effect_remaining = random.uniform(30, 180)
                    self.anode_effect_timer = 0
        else:
            self.anode_effect_remaining -= dt
            if self.anode_effect_remaining <= 0:
                self.in_anode_effect = False
                self.anode_effect_cooldown = random.uniform(3600, 72000)
                self.alumina_concentration += 0.3

        if self.in_anode_effect:
            self.voltage = random.uniform(20, 40)
        else:
            voltage_noise = random.gauss(0, 0.02)
            if self.alumina_concentration < 2.0:
                voltage_noise += random.gauss(0, 0.1) * (2.0 - self.alumina_concentration)
            self.voltage = self.voltage_base + voltage_noise

    def get_data(self):
        return {
            "cellId": self.cell_id,
            "voltage": round(self.voltage, 4),
            "anodeCurrentDistribution": json.dumps(
                [round(c, 3) for c in self.anode_currents]
            ),
            "cellTemperature": round(self.cell_temperature, 2),
            "bathTemperature": round(self.bath_temperature, 2),
            "aluminumLevel": round(self.aluminum_level, 2),
            "bathLevel": round(self.bath_level, 2),
            "aluminaConcentration": round(self.alumina_concentration, 4),
            "anodeEffect": self.in_anode_effect,
            "timestamp": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        }


class ZigBeeSimulator:
    def __init__(self, api_url, num_cells, interval):
        self.api_url = api_url.rstrip("/")
        self.batch_url = f"{self.api_url}/api/celldata/batch"
        self.num_cells = num_cells
        self.interval = interval
        self.cells = [ElectrolyticCell(i + 1) for i in range(num_cells)]
        self.running = True
        self.cycle_count = 0
        self.success_count = 0
        self.fail_count = 0

    def send_batch(self, batch_data):
        try:
            resp = requests.post(
                self.batch_url,
                json=batch_data,
                timeout=10,
                headers={"Content-Type": "application/json"},
            )
            if resp.status_code == 200:
                self.success_count += 1
            else:
                self.fail_count += 1
                print(
                    f"[{datetime.now().strftime('%H:%M:%S')}] 发送失败 HTTP {resp.status_code}"
                )
        except requests.RequestException as e:
            self.fail_count += 1
            print(f"[{datetime.now().strftime('%H:%M:%S')}] 连接失败: {e}")

    def run(self):
        print("=" * 60)
        print("  电解槽模拟器启动")
        print(f"  模拟槽数: {self.num_cells}")
        print(f"  发送间隔: {self.interval}秒")
        print(f"  API地址: {self.api_url}")
        print("=" * 60)

        while self.running:
            self.cycle_count += 1
            cycle_start = time.time()

            anode_effect_cells = []
            for cell in self.cells:
                cell.update(self.interval)
                if cell.in_anode_effect:
                    anode_effect_cells.append(cell.cell_id)

            batch_data = [cell.get_data() for cell in self.cells]

            sender = threading.Thread(target=self.send_batch, args=(batch_data,), daemon=True)
            sender.start()

            ts = datetime.now().strftime("%H:%M:%S")
            ae_info = ""
            if anode_effect_cells:
                ae_info = f" | 阳极效应: {anode_effect_cells[:5]}"
                if len(anode_effect_cells) > 5:
                    ae_info += f"...等{len(anode_effect_cells)}个槽"

            low_alumina = sum(
                1 for c in self.cells if c.alumina_concentration < 2.0
            )

            print(
                f"[{ts}] 第{self.cycle_count}轮 发送数据 "
                f"成功:{self.success_count} 失败:{self.fail_count} "
                f"低氧化铝:{low_alumina}{ae_info}"
            )

            elapsed = time.time() - cycle_start
            sleep_time = max(0, self.interval - elapsed)
            time.sleep(sleep_time)


def main():
    parser = argparse.ArgumentParser(description="ZigBee电解槽模拟器")
    parser.add_argument(
        "--url",
        default="http://localhost:5000",
        help="后端API地址 (默认: http://localhost:5000)",
    )
    parser.add_argument(
        "--cells", type=int, default=200, help="模拟电解槽数量 (默认: 200)"
    )
    parser.add_argument(
        "--interval", type=int, default=15, help="数据发送间隔秒数 (默认: 15)"
    )
    args = parser.parse_args()

    simulator = ZigBeeSimulator(args.url, args.cells, args.interval)

    try:
        simulator.run()
    except KeyboardInterrupt:
        print("\n模拟器已停止")
        print(f"总计发送: {simulator.cycle_count}轮 成功:{simulator.success_count} 失败:{simulator.fail_count}")


if __name__ == "__main__":
    main()
