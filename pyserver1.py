import asyncio
import websockets
import json

async def echo(websocket, path):
    async for message in websocket:
        print(f"Received message")
        
        # Define the JSON response
        response = {
            "Timestamp": "2023-10-01T12:34:56Z",
            "Protocol": "WebSocket",
            "Success": True,
            "RequestId": "12345",
            "SessionId": "abcde",
            "ErrorCode": None,
            "ErrorMessage": None,
            "EndPoint": "ws://localhost:8080",
            "Data": [
                "response from python 1"
            ]
        }
        
        # Convert the response to a JSON string
        response_json = json.dumps(response)
        
        # Send the JSON response back to the client
        await websocket.send(response_json)

start_server = websockets.serve(echo, "localhost", 5001)

asyncio.get_event_loop().run_until_complete(start_server)
asyncio.get_event_loop().run_forever()

"""
 
"""