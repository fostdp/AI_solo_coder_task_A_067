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
        self.injected_depletion = 0.0
        self.voltage_spike_active = False
        self.voltage_spike_count = 0
        self.voltage_spike_remaining = 0

    def inject_concentration_drop(self, magnitude=1.0):
        self.injected_depletion += magnitude

    def inject_anode_effect_precursor(self, duration_cycles=10):
        self.voltage_spike_active = True
        self.voltage_spike_remaining = duration_cycles
        self.voltage_spike_count = 0

    def update(self, dt):
        effective_depletion = self.alumina_depletion_rate * dt + self.injected_depletion
        self.injected_depletion *= 0.9
        if self.injected_depletion < 0.0001:
            self.injected_depletion = 0.0

        self.alumina_concentration -= effective_depletion * dt
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

            if self.voltage_spike_active:
                self.voltage_spike_remaining -= 1
                self.voltage_spike_count += 1
                if random.random() < 0.3:
                    voltage_noise += random.uniform(0.1, 0.4)
                if random.random() < 0.15:
                    voltage_noise += random.uniform(0.3, 0.8)
                if self.voltage_spike_remaining <= 0:
                    self.voltage_spike_active = False

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


class InjectionManager:
    def __init__(self, cells):
        self.cells = {c.cell_id: c for c in cells}
        self.scheduled = []

    def schedule_concentration_drop(self, cell_ids, magnitude=1.0, at_cycle=None):
        self.scheduled.append({
            "type": "concentration_drop",
            "cell_ids": cell_ids,
            "magnitude": magnitude,
            "at_cycle": at_cycle,
        })

    def schedule_anode_effect_precursor(self, cell_ids, duration_cycles=10, at_cycle=None):
        self.scheduled.append({
            "type": "anode_effect_precursor",
            "cell_ids": cell_ids,
            "duration_cycles": duration_cycles,
            "at_cycle": at_cycle,
        })

    def process(self, current_cycle):
        for inj in self.scheduled:
            if inj["at_cycle"] is not None and inj["at_cycle"] != current_cycle:
                continue
            if inj["at_cycle"] is None and current_cycle < 3:
                continue

            for cid in inj["cell_ids"]:
                cell = self.cells.get(cid)
                if not cell:
                    continue
                if inj["type"] == "concentration_drop":
                    cell.inject_concentration_drop(inj["magnitude"])
                elif inj["type"] == "anode_effect_precursor":
                    cell.inject_anode_effect_precursor(inj["duration_cycles"])

        self.scheduled = [
            s for s in self.scheduled
            if s["at_cycle"] is not None and s["at_cycle"] > current_cycle
        ]


def load_injection_config(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except FileNotFoundError:
        return None
    except json.JSONDecodeError as e:
        print(f"注入配置文件解析失败: {e}")
        return None


class ZigBeeSimulator:
    def __init__(self, api_url, num_cells, interval, injection_config=None):
        self.api_url = api_url.rstrip("/")
        self.batch_url = f"{self.api_url}/api/celldata/batch"
        self.num_cells = num_cells
        self.interval = interval
        self.cells = [ElectrolyticCell(i + 1) for i in range(num_cells)]
        self.running = True
        self.cycle_count = 0
        self.success_count = 0
        self.fail_count = 0
        self.injection_manager = InjectionManager(self.cells)

        if injection_config:
            self._load_injections(injection_config)

    def _load_injections(self, config):
        for inj in config.get("concentration_drops", []):
            cell_ids = inj.get("cell_ids", [])
            if inj.get("all_cells"):
                cell_ids = list(range(1, self.num_cells + 1))
            elif inj.get("random_count"):
                cell_ids = random.sample(range(1, self.num_cells + 1), inj["random_count"])
            self.injection_manager.schedule_concentration_drop(
                cell_ids=cell_ids,
                magnitude=inj.get("magnitude", 1.0),
                at_cycle=inj.get("at_cycle"),
            )

        for inj in config.get("anode_effect_precursors", []):
            cell_ids = inj.get("cell_ids", [])
            if inj.get("random_count"):
                cell_ids = random.sample(range(1, self.num_cells + 1), inj["random_count"])
            self.injection_manager.schedule_anode_effect_precursor(
                cell_ids=cell_ids,
                duration_cycles=inj.get("duration_cycles", 10),
                at_cycle=inj.get("at_cycle"),
            )

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
                    f"[{datetime.now().strftime('%H:%M:%S')}] HTTP {resp.status_code}"
                )
        except requests.RequestException as e:
            self.fail_count += 1
            print(f"[{datetime.now().strftime('%H:%M:%S')}] {e}")

    def run(self):
        print("=" * 60)
        print("  ZigBee Electrolytic Cell Simulator")
        print(f"  Cells: {self.num_cells}")
        print(f"  Interval: {self.interval}s")
        print(f"  API: {self.api_url}")
        print(f"  Injections: {len(self.injection_manager.scheduled)}")
        print("=" * 60)

        while self.running:
            self.cycle_count += 1
            cycle_start = time.time()

            self.injection_manager.process(self.cycle_count)

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
                ae_info = f" | AE: {anode_effect_cells[:5]}"
                if len(anode_effect_cells) > 5:
                    ae_info += f"...+{len(anode_effect_cells) - 5}"

            low_alumina = sum(
                1 for c in self.cells if c.alumina_concentration < 2.0
            )

            print(
                f"[{ts}] #{self.cycle_count} OK:{self.success_count} FAIL:{self.fail_count} "
                f"LowAl2O3:{low_alumina}{ae_info}"
            )

            elapsed = time.time() - cycle_start
            sleep_time = max(0, self.interval - elapsed)
            time.sleep(sleep_time)


def main():
    parser = argparse.ArgumentParser(description="ZigBee Electrolytic Cell Simulator")
    parser.add_argument(
        "--url",
        default="http://alcell-api:5000",
        help="Backend API URL (default: http://alcell-api:5000)",
    )
    parser.add_argument(
        "--cells", type=int, default=200, help="Number of cells (default: 200)"
    )
    parser.add_argument(
        "--interval", type=int, default=15, help="Send interval in seconds (default: 15)"
    )
    parser.add_argument(
        "--injection",
        default=None,
        help="Path to injection config JSON file"
    )
    args = parser.parse_args()

    injection_config = None
    if args.injection:
        injection_config = load_injection_config(args.injection)
        if injection_config:
            print(f"Loaded injection config: {args.injection}")

    simulator = ZigBeeSimulator(args.url, args.cells, args.interval, injection_config)

    try:
        simulator.run()
    except KeyboardInterrupt:
        print(f"\nStopped. Cycles:{simulator.cycle_count} OK:{simulator.success_count} FAIL:{simulator.fail_count}")


if __name__ == "__main__":
    main()
