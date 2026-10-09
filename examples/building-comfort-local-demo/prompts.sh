# Shared prompts for demo.sh and e2e.sh. One prompt is one A2A task.
# The comfort definition matches the Drasi building-comfort tutorial. It is
# caller language, not Cypher.

PROMPT_ROOM='Tell me when a room is uncomfortable. A room comfort level is 50 plus (temperature - 72) plus (humidity - 42), plus (co2 - 500) / 25 when co2 is above 500, otherwise no co2 extra. Round that level down to a whole number. Uncomfortable means the level is below 40 or above 50. Subscribe on this task.'

PROMPT_FLOOR='Tell me when a floor average room comfort level is uncomfortable. A room comfort level is 50 plus (temperature - 72) plus (humidity - 42), plus (co2 - 500) / 25 when co2 is above 500, otherwise no co2 extra. Round each room level down, then average the rooms on that floor. Uncomfortable means that average is below 40 or above 50. Subscribe on this task.'

PROMPT_BUILDING='Tell me when the building comfort level is uncomfortable. A room comfort level is 50 plus (temperature - 72) plus (humidity - 42), plus (co2 - 500) / 25 when co2 is above 500, otherwise no co2 extra. Round each room level down, average the rooms on each floor, then average those floor levels. Uncomfortable means that building average is below 40 or above 50. Subscribe on this task.'
