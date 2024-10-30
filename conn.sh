#!/bin/bash

# Define the WebSocket server URL
WEBSOCKET_URL="ws://localhost:5000"

# Define the JSON message
JSON_MESSAGE='{
    "Timestamp": "2023-10-01T12:34:56Z",
    "Protocol": "WebSocket",
    "Success": true,
    "RequestId": "12345",
    "SessionId": "abcde",
    "ErrorCode": null,
    "ErrorMessage": null,
    "EndPoint": "ws://localhost:5000",
    "Data": ["example data 1", "example data 2"]
}'

# Connect to the WebSocket server and send the JSON message
echo "$JSON_MESSAGE" | websocat "$WEBSOCKET_URL"