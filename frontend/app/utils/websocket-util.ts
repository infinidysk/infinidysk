export function receiveMessage(
  onMessage: (topic: string, message: string) => void,
): (event: MessageEvent) => void {
  return (event) => {
    // Websocket text frames carry the backend envelope shape { Topic, Message }
    const parsed = JSON.parse(event.data as string) as { Topic: string; Message: string };
    onMessage(parsed.Topic, parsed.Message);
  };
}
