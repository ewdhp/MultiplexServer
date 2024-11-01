import asyncio
import websockets
import json

async def echo(websocket, path):
    async for message in websocket:
        print(f"Received message")
        
        # Define the JSON response
        response = {
            "Data": [
                "response from python"
            ]
        }
        
        # Convert the response to a JSON string
        response_json = json.dumps(response)
        
        # Send the JSON response back to the client
        await websocket.send(response_json)

start_server = websockets.serve(echo, "localhost", 5000)

asyncio.get_event_loop().run_until_complete(start_server)
asyncio.get_event_loop().run_forever()