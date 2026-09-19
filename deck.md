# Kitbash

The art of building has been lost.

Every year, fewer of us go outside, take things apart, and work out how to build something with our hands. Kids now play outside half as much as their parents did. In England, half as many students take design and technology, their shop class, as in 2009.

Kitbash brings building back. We make the whole world your kit. Put on a Meta Quest 3, and it finds every object in front of you, measures it, and turns it into a true-size hologram, a digital twin. Ask Kit, our OpenAI-powered copilot, "what can I build?" and it designs something from exactly what you have, like a birdhouse from an energy drink can and a box. Kit lights up the pieces you need, flies their holograms into place, and talks you through every step in an ElevenLabs voice, so your hands stay free. You build by putting each real object into its hologram. It feels like a game where the room is your box of parts.

Kitbash is for kids, and the table is only the start. Next, we're taking it outside, where sticks, rocks and old boxes become a fort or a treehouse, with a hologram showing where every piece goes. Less scrolling, more building.

## The 3-minute demo

Three presenters, one judge wearing the headset, and the other judges watching the headset's view cast to the laptop. Quoted lines are spoken as written; italics are what happens. The times are estimates at a normal speaking pace and add up to about two and a half minutes. The spare half minute is room for slow AI answers, so don't fill it.

### Who does what

| Who | Role | Where | Job |
|---|---|---|---|
| Michael | Host and Operator | At the laptop, facing the judges | The problem, the hybrid line and the close. Between lines, runs the cast and the Director page, and fixes anything that breaks without a word. Keeps time. |
| Rhythm | Engineer | Beside the laptop | The solution, then what Kit is doing while the judge waits. Talks to the judges watching the cast, not the one in the headset. |
| Henry | Guide | At the shoulder of the judge in the headset | Fits the headset and gives the judge every instruction, word for word. Puts the judge's thumb on each button. |

### Before you walk in

- API keys in `.env.local` (OpenAI, ElevenLabs, and the OMNI key from yibuapi), `COPILOT_MODE=live`, and a small, fast model in `OPENAI_ROUTER_MODEL`. The router gets 700 ms, and a timeout turns "something crazier" into a plain question.
- Headset charged, app open, camera and spatial-data permissions already granted, passthrough on, strap loosened, volume at maximum so the room hears Kit.
- Keep it awake off the face: `adb shell am broadcast -a com.oculus.vrpowermanager.prox_close`.
- Run the whole demo once in the hallway with the same objects. It warms the AI's cache, and `pnpm build:record` saves a real scan for the replay fallback.
- Keep the kit on a tray (the Monster can and everything else you rehearsed with) so it goes down on the table in one move.
- Laptop and headset on the same network, with a phone hotspot ready as backup.

### Setup in the room (the first minute, before the clock)

Tray on the table. The laptop shows the cast full screen, turned toward the judges, with the Director page in a second tab. Henry holds the headset and both controllers.

### Problem (Michael, about 20 seconds)

**Michael:** "The art of building has been lost. Kids play outside half as much as their parents did. In England, half as many students take shop class as in 2009. Fewer and fewer of us ever take something apart and turn it into something new."

### Solution (Rhythm, about 20 seconds)

*Henry fits the headset on a judge while Rhythm talks. Ask about glasses: the Quest 3 fits over them.*

**Rhythm:** "So we built Kitbash. Remember the master builders in The Lego Movie, who look at a pile of junk and see a spaceship? Kitbash gives every kid that power, and Kit, our AI copilot, shows them how."

*Rhythm holds up the Monster can.*

**Rhythm:** "This is an energy drink. In a minute, one of you is going to build something with it."

If the headset isn't on yet, Rhythm fills: "While that goes on: when did you last build something with your hands?"

### Demo

**Scan and ask (about 40 seconds)**

**Henry:** "Look at the table. Press A on your right controller, ask Kit — what can I build? — and press A again."

*The judge asks. "Scanning… hold still." Outlines appear around every object, then each one gets a name and a size.*

**Rhythm** (pointing at the cast): "Kit names each one and snaps it to its real size: a digital twin of everything on the table. Our design rules answer first. Then OpenAI designs its own builds from exactly these objects, and every design is checked for balance, so nothing it suggests falls over."

*Kit says what it found, and three designs appear above the table.*

**Henry:** "Point at the birdhouse until it grows, then pull the trigger."

**Build the birdhouse (about 30 seconds)**

*The holograms fly off the real objects and form the birdhouse. Step one, the can, glows, and Kit reads it out.*

**Henry:** "Pick up the real can and stand it inside its hologram." *The judge does.* "Now press A, say done, press A again."

*Step two glows, and Kit reads it out.*

**Michael** (to all the judges): "That's the hybrid: half real, half hologram. Kit reads every step out loud, so your hands stay free to build."

**Something crazier (about 25 seconds)**

**Henry:** "Press A and ask Kit for something crazier."

**Rhythm** (while it works): "Nobody wrote this design. OpenAI is inventing it live from the same objects, and our solver fits it to their measured sizes, so the real pieces actually fit."

*New designs appear.* **Henry:** "Pick the craziest one." *It flies together.*

### Close (Michael, about 10 seconds)

**Michael:** "Kitbash. Today, a birdhouse on a table. Tomorrow, a fort in the backyard. The whole world is your kit. Thank you. We'd love your questions."

### If something breaks

Michael fixes it from the laptop in silence while the others keep going.

| What happens | Michael, at the laptop | Who says what |
|---|---|---|
| The scan misses an object | Director, Build, add the missed object | Nothing |
| No designs after 10 seconds | Director, Build, Start on the birdhouse | Henry: "Let's go straight to the birdhouse." |
| "Something crazier" brings nothing new | Director, Build, Start on another design | Rhythm drops "Nobody wrote this design" and "live" |
| The headset or the cast dies | Director, Build, replay the hallway recording on the laptop | Michael: "Here's the same run, recorded just before we came in." Always say it's a recording. |

### Timing

Rhythm's explanations are the slack: if Kit answers early, stop mid-sentence and move on. If the designs are slow, the judge can pick the birdhouse as soon as it appears; it's a rule design, so it arrives before the AI's. Round 1 cuts off at five minutes, setup and questions included.

### Likely questions

- **Is it live?** The scan, the names, the balance check and Kit's voice are live, and "something crazier" is always a fresh OpenAI call. Designs for "what can I build?" are cached per set of objects after the first run.
- **Isn't a headset the opposite of getting kids outside?** Nothing happens until you pick up a real object. The headset shows the plan; your hands do the building, and what you build is real.
- **Can it go outside?** Meta warns that direct sunlight can damage the headset's displays, so today it's for shade: a porch, a garage, a table under a tree.
- **Is a Quest OK for kids?** Meta allows ages 10 and up, with an account a parent manages.
- **How does it know it won't fall over?** Every part rests flat on what's under it, to within 2 mm, and a balance check makes sure each part's weight lands on what holds it up.
- **Why a headset and not a phone?** You need both hands to build. The hologram sits at true size exactly where each piece goes, and you talk instead of tapping.

### Not built yet (this script needs them)

Build mode lives on the `build-mode` branch: not merged, not pushed, and never run on a headset or against the real models. Until the keys are in `.env.local`, Kit is off.

| For | What | Hours |
|---|---|---|
| Everything | Keys, live mode and a small router model; run the whole flow on the Quest and tune it | 3–5 |
| The Huawei prize | Qwen3.5-Omni through yibuapi on a real voice turn: the judge's voice clip and the headset's camera photo in, a spoken answer out | 2–4 |
| The birdhouse | A birdhouse rule fitted to our real objects (rules always rank above AI designs, so it shows every time) | 1–2 |
| Something crazier | Allow a new design mid-build: the server refuses it today, and the headset ignores it | 1.5–2 |
| Kit's name | The copilot's prompt still starts "You are Cut Once, a construction copilot" (`services/api/src/copilot/prompt.ts:12`), so a judge who asks its name hears "Cut Once" | 0.25 |
| Knowing Kit heard you | A listening and thinking badge while A is held and while Kit works | 1 |
| "Lights up the pieces you need" | Highlight the real objects a design and each step use | 1.5–2 |
| Fewer surprises | The app opens on E7 by default (server seed and the headset's offline fallback); a trigger that misses a preview clears all three; the HUD keeps showing the last run's step; the Monster can's size | 1.5–2 |

Worth it if there's time: a live feed of what Kit is doing while it thinks (2–3), hologram-only parts such as a peaked roof (3–5).

## Tracks

- Hack the North 2026: Finalists (main award)
- Huawei: OMNI Live Challenge

## To qualify

- Every sponsor prize must be selected on Devpost before Saturday 2:00 PM EDT. Prizes added later don't count.
- Huawei OMNI Live: a working AI app on an edge device (a wearable counts) that uses an OMNI multimodal model through its cloud API and meaningfully uses all three of vision, speech and language, in at least one complete end-to-end scenario. Concepts and mockups don't count. Judged on scenario creativity, use of OMNI's multimodal abilities, demo completeness, interaction quality and technical implementation. Today every model call goes to OpenAI, so OMNI has to be wired in.

## Where the numbers come from

- Outdoor play: National Trust survey of 1,001 UK parents (2016). Children played outside about 4 hours a week, against 8.2 hours for their parents as children.
- Shop class: Education Policy Institute, "A spotlight on Design and Technology study in England" (2022). GCSE entries fell from 280,670 (44% of students) in 2009 to 136,150 (22%) in 2020.
