import asyncio
import json
import math
import random
import time
from datetime import datetime, timezone
import aiohttp

API_URL = "http://localhost:5000/api/cells/data"
NUM_CELLS = 200
INTERVAL = 15
BATCH_SIZE = 20


class CellSimulator:
    def __init__(self, cell_id):
        self.cell_id = cell_id
        self.concentration = random.uniform(2.5, 4.0)
        self.voltage_base = 4.0 + random.uniform(-0.1, 0.1)
        self.current_base = 300 + random.uniform(-10, 10)
        self.cell_temp_base = 960 + random.uniform(-5, 5)
        self.bath_temp_base = 955 + random.uniform(-5, 5)
        self.al_level = 22 + random.uniform(-2, 2)
        self.bath_level = 18 + random.uniform(-2, 2)
        self.phase = random.uniform(0, 2 * math.pi)
        self.consumption_rate = random.uniform(0.005, 0.02)
        self.anode_effect_risk = 0.0
        self.feed_cooldown = 0

    def generate_data(self, tick):
        self.concentration -= self.consumption_rate
        if self.feed_cooldown > 0:
            self.feed_cooldown -= 1
            self.concentration += 0.02

        if self.concentration < 1.0 and self.feed_cooldown <= 0:
            self.concentration += random.uniform(1.0, 2.0)
            self.feed_cooldown = 20

        self.concentration = max(0.3, min(6.0, self.concentration))

        voltage_noise = random.gauss(0, 0.01 + max(0, 2.5 - self.concentration) * 0.02)
        voltage = self.voltage_base + voltage_noise
        if self.concentration < 1.5:
            voltage += (1.5 - self.concentration) * 0.3
        voltage += 0.01 * math.sin(tick * 0.1 + self.phase)

        if self.concentration < 1.0:
            self.anode_effect_risk = min(1.0, self.anode_effect_risk + 0.05)
            if random.random() < self.anode_effect_risk * 0.02:
                voltage += random.uniform(2.0, 8.0)
                self.anode_effect_risk = 0.8
        else:
            self.anode_effect_risk = max(0, self.anode_effect_risk - 0.02)

        num_anodes = 24
        currents = []
        for i in range(num_anodes):
            base = self.current_base / num_anodes
            spread = max(0, 2.0 - self.concentration) * 2
            current = base + random.gauss(0, 0.5 + spread)
            currents.append(round(current, 2))

        cell_temp = self.cell_temp_base + random.gauss(0, 1)
        bath_temp = self.bath_temp_base + random.gauss(0, 1)
        al_level = self.al_level + random.gauss(0, 0.1)
        bath_level = self.bath_level + random.gauss(0, 0.1)

        return {
            "cellId": self.cell_id,
            "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
            "voltage": round(voltage, 3),
            "anodeCurrentDistribution": ",".join(str(c) for c in currents),
            "cellTemp": round(cell_temp, 1),
            "bathTemp": round(bath_temp, 1),
            "alLevel": round(al_level, 1),
            "bathLevel": round(bath_level, 1)
        }


async def send_batch(session, data_list):
    try:
        async with session.post(API_URL, json=data_list) as resp:
            if resp.status != 200:
                text = await resp.text()
                print(f"  Error sending data: {resp.status} - {text[:100]}")
    except Exception as e:
        print(f"  Connection error: {e}")


async def main():
    print("=" * 60)
    print("  电解铝电解槽 ZigBee 模拟器")
    print(f"  模拟电解槽数量: {NUM_CELLS}")
    print(f"  数据上报间隔: {INTERVAL}秒")
    print(f"  目标API: {API_URL}")
    print("=" * 60)

    simulators = [CellSimulator(i + 1) for i in range(NUM_CELLS)]
    tick = 0

    async with aiohttp.ClientSession(
        headers={"Content-Type": "application/json"}
    ) as session:
        while True:
            tick += 1
            start_time = time.time()
            all_data = [sim.generate_data(tick) for sim in simulators]

            tasks = []
            for i in range(0, len(all_data), BATCH_SIZE):
                batch = all_data[i:i + BATCH_SIZE]
                for data in batch:
                    tasks.append(send_batch(session, data))

            await asyncio.gather(*tasks)

            elapsed = time.time() - start_time
            conc_values = [s.concentration for s in simulators]
            low_count = sum(1 for c in conc_values if c < 1.8)
            critical_count = sum(1 for c in conc_values if c < 1.5)
            avg_conc = sum(conc_values) / len(conc_values)

            timestamp = datetime.now().strftime("%H:%M:%S")
            print(f"[{timestamp}] Tick #{tick}: "
                  f"200 cells | Avg Conc: {avg_conc:.2f}% | "
                  f"Low(<1.8%): {low_count} | Critical(<1.5%): {critical_count} | "
                  f"Sent in {elapsed:.2f}s")

            wait_time = max(0, INTERVAL - elapsed)
            await asyncio.sleep(wait_time)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\n模拟器已停止")
