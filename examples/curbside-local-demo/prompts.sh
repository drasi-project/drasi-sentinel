# Shared prompts for demo.sh and e2e.sh. One prompt is one A2A task.

PROMPT_PREPARING='Tell me when a pickup order is still being prepared. Keep the watch for 7 days.'

PROMPT_READY='Tell me when a pickup order is ready for pickup. Keep the watch for 7 days.'

PROMPT_PARKING='Tell me when a pickup vehicle is in the parking lot. Keep the watch for 7 days.'

PROMPT_CURBSIDE='Tell me when a pickup vehicle is at the curb. Keep the watch for 7 days.'

PROMPT_MATCHED='Tell me when an order is ready for pickup and the vehicle with the same plate is at the curb. Keep the watch for 7 days.'

PROMPT_DELAYED='Tell me when a vehicle has been waiting at the curb for 10 seconds and its order is still being prepared. Keep the watch for 7 days.'

# "waiting" is not a stored field. The e2e must not turn this into a watch.
PROMPT_VAGUE='Tell me when an order is ready and the customer is waiting. Keep the watch for 7 days.'
