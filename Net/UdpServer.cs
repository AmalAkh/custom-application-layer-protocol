using System.Diagnostics;
using System.Net;
using Timers = System.Timers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using CustomProtocol.Net.Exceptions;


namespace CustomProtocol.Net
{
    public enum UdpServerStatus
    {
        Unconnected, Connected, WaitingForIncomingConnectionAck, WaitingForOutgoingConnectionAck
    }
    public class CustomUdpClient
    {
       

        
        private Connection _connection;

        protected Socket _sendingSocket;
        protected Socket _listeningSocket;
        public UdpServerStatus status;
        public UdpServerStatus Status
        {
            get;
        }

        private Dictionary<uint, List<CustomProtocolMessage>> _fragmentedMessages = new Dictionary<uint, List<CustomProtocolMessage>>();
        private Dictionary<uint, List<CustomProtocolMessage>> _bufferedFragmentedMessages = new Dictionary<uint, List<CustomProtocolMessage>>();

        public CustomUdpClient()
        {
           
        }
        
        public void Start(string address, ushort listeningPort, ushort sendingPort)
        {
            _listeningSocket = new Socket(AddressFamily.InterNetwork,SocketType.Dgram, ProtocolType.Udp);
            _listeningSocket.Bind(new IPEndPoint(IPAddress.Parse(address), listeningPort));

            _sendingSocket = new Socket(AddressFamily.InterNetwork,SocketType.Dgram, ProtocolType.Udp);
            _sendingSocket.Bind(new IPEndPoint(IPAddress.Parse(address), sendingPort));
            _connection = new Connection(_listeningSocket, _sendingSocket);
            StartListening();
            Console.WriteLine($"Listening on address {address} on port {listeningPort}");
            Console.WriteLine($"Sending using address {address} on port {sendingPort}");


            
        }
        
        protected void StartListening()
        {
           
        
            Task task = new Task(async()=>
            {

                while(true)
                {
                    byte[] bytes = new byte[1500];//buffer
                    IPEndPoint endPoint = new IPEndPoint(IPAddress.None,0);
                    
                    SocketReceiveFromResult receiveFromResult = await _listeningSocket.ReceiveFromAsync(bytes, endPoint);
                    var senderEndPoint = receiveFromResult.RemoteEndPoint as IPEndPoint;
                    //Console.WriteLine($"Received - {receiveFromResult.ReceivedBytes}");

                    CustomProtocolMessage incomingMessage = new CustomProtocolMessage();
                    try
                    {
                        incomingMessage = CustomProtocolMessage.FromBytes(bytes.Take(receiveFromResult.ReceivedBytes).ToArray());
                    }catch(DamagedMessageException e)
                    {
                        Console.WriteLine("Fragment damaged");
                        continue;
                    }
                    if(_connection.IsConnectionTimeout)
                    {   
                        await _connection.InterruptConnectionHandshake();
                    
                    }else if(_connection.IsConnectionInterrupted)
                    {    await _connection.InterruptConnection();

                    }else if(_connection.Status == ConnectionStatus.Unconnected &&  incomingMessage.Syn && !incomingMessage.Ack)
                    {
                        await _connection.AcceptConnection(new IPEndPoint(senderEndPoint.Address, BitConverter.ToInt16(incomingMessage.Data) ));
                    }else if(_connection.Status == ConnectionStatus.WaitingForIncomingConnectionAck && incomingMessage.Ack && !incomingMessage.Syn)
                    {
                                                
                        await _connection.EstablishConnection();
                    }else if(_connection.Status == ConnectionStatus.WaitingForOutgoingConnectionAck && incomingMessage.Ack && incomingMessage.Syn)
                    {
                        await _connection.EstablishOutgoingConnection(new IPEndPoint(senderEndPoint.Address, BitConverter.ToInt16(incomingMessage.Data) ));
                    }else if(_connection.Status == ConnectionStatus.Connected && incomingMessage.Pong)
                    {
                        _connection.ReceivePong();
                    }else if(_connection.Status == ConnectionStatus.Connected && incomingMessage.Finish)
                    {
                        await _connection.AcceptDisconnection();
                    }
                    else if(_connection.Status == ConnectionStatus.Connected && incomingMessage.Ack && !incomingMessage.Syn)
                    {
                        if(_unAcknowledgedMessages.ContainsKey(incomingMessage.Id))
                        {
                            //Console.WriteLine($"Send fragment #{incomingMessage.SequenceNumber} acknowledged");

                            _unAcknowledgedMessages[incomingMessage.Id].Remove(incomingMessage.SequenceNumber);
                        }
                    }else if(incomingMessage.Ping)
                    {
                        await _connection.SendPong();
                    }else if(_connection.Status == ConnectionStatus.Connected)
                    {
                        await HandleMessage(incomingMessage);
                    }
                    

                }
            });
            task.Start();
        }
        private Dictionary<UInt16, UInt32> _overrallMessagesCount = new Dictionary<UInt16, UInt32>();
        
        
        private async Task HandleMessage(CustomProtocolMessage incomingMessage)
        {
            if(incomingMessage.Last && incomingMessage.SequenceNumber == 0)
            {
                Console.WriteLine("New message:");
                Console.WriteLine(Encoding.ASCII.GetString(incomingMessage.Data));
            }else 
            {
                Console.WriteLine(incomingMessage.SequenceNumber);
                Console.WriteLine(Encoding.ASCII.GetString(incomingMessage.Data));

                if(incomingMessage.Last)
                {
                    _overrallMessagesCount[incomingMessage.Id] = (UInt16)(incomingMessage.SequenceNumber+1);
                    _fragmentedMessages[incomingMessage.Id].Add(incomingMessage);

                    
                }else if(incomingMessage.SequenceNumber == 20)
                {
                    AddToFragmentedMessages(incomingMessage);
                    Console.WriteLine("added");
                    _fragmentedMessages[incomingMessage.Id] = _fragmentedMessages[incomingMessage.Id].OrderBy((fragment)=>fragment.SequenceNumber).ToList();
                    if(!_bufferedFragmentedMessages.ContainsKey(incomingMessage.Id))
                    {
                        
                     
                        _bufferedFragmentedMessages.Add(incomingMessage.Id, new List<CustomProtocolMessage>());
                       
                    }
                    foreach(var fragmentMsg in _fragmentedMessages[incomingMessage.Id])
                    {
                        fragmentMsg.InternalSequenceNum = fragmentMsg.SequenceNumber+ 20* (int)(_bufferedFragmentedMessages[incomingMessage.Id].Count/20);
                        _bufferedFragmentedMessages[incomingMessage.Id].Add(fragmentMsg);
                    }
                    _fragmentedMessages[incomingMessage.Id].Clear();
                }else
                {
                    AddToFragmentedMessages(incomingMessage);
                }
                if(_overrallMessagesCount[incomingMessage.Id] != 0 && _overrallMessagesCount[incomingMessage.Id] == _fragmentedMessages[incomingMessage.Id].Count)
                {
                    AssembleFragments(incomingMessage.Id, false);
                }
            }
            await _connection.SendFragmentAcknoledgement(incomingMessage.Id, incomingMessage.SequenceNumber);
          //  Console.WriteLine($"Received fragment #{incomingMessage.SequenceNumber} acknowledged");
        }
        private void AddToFragmentedMessages(CustomProtocolMessage incomingMessage)
        {
            
            if(_fragmentedMessages.ContainsKey(incomingMessage.Id))
            {
                _fragmentedMessages[incomingMessage.Id].Add(incomingMessage);
                

            }else
            {
                _fragmentedMessages.Add(incomingMessage.Id, new List<CustomProtocolMessage>());
                _overrallMessagesCount.Add(incomingMessage.Id, 0);
                _fragmentedMessages[incomingMessage.Id].Add(incomingMessage);

            }
        }
        private async void AssembleFragments(uint id, bool isFile = false)
        {
            List<byte> defragmentedBytes = new List<byte>();
            _fragmentedMessages[id] = _fragmentedMessages[id].OrderBy((fragment)=>fragment.SequenceNumber).ToList();
            
            if(_bufferedFragmentedMessages.ContainsKey(id))
            {
                
                foreach(CustomProtocolMessage msg in _bufferedFragmentedMessages[id].OrderBy((fragment)=>fragment.InternalSequenceNum).ToList())
                {
                    foreach(byte oneByte in msg.Data)
                    {
                      //  Console.WriteLine(Encoding.ASCII.GetString(msg.Data));
                        defragmentedBytes.Add(oneByte);
                    }
                }
            }
            foreach(CustomProtocolMessage msg in _fragmentedMessages[id])
            {
                foreach(byte oneByte in msg.Data)
                {
                   // Console.WriteLine(Encoding.ASCII.GetString(msg.Data));

                    defragmentedBytes.Add(oneByte);
                }
            }
            if(!isFile)
            {
                //Console.WriteLine("New message");
                Console.WriteLine(Encoding.ASCII.GetString(defragmentedBytes.ToArray()));
            }
        }
        private Dictionary<UInt16, List<uint>> _unAcknowledgedMessages = new Dictionary<UInt16, List<uint>>();
        private int _windowSize = 4;
    
       
        public async Task SendTextMessage(string text, uint fragmentSize = 5)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            UInt16 id = (UInt16)Random.Shared.Next(0,Int16.MaxValue);
            UInt16 seqNum = 0;
            _unAcknowledgedMessages.Add(id, new List<uint>());
            if(bytes.Length <= fragmentSize)
            {
                CustomProtocolMessage message = new CustomProtocolMessage();
                
                message.SetFlag(CustomProtocolFlag.Last, true);
                message.Data = bytes;
                await _connection.SendMessage(message);
                Console.WriteLine("Message sent");
            }else
            {   

                await Task.Run(async ()=>
                {
                    List<List<CustomProtocolMessage>> fragmentsToSend = new List<List<CustomProtocolMessage>>();
                    int currentWindowStart = 0;
                    int currentWindowEnd = currentWindowStart+_windowSize-1;
                    int currentFragmentListIndex = 0;
                    fragmentsToSend.Add(new List<CustomProtocolMessage>());
                    for(ushort i = 0; i < bytes.Length; i+=(ushort)fragmentSize)
                    {   
                        Console.WriteLine(i);   
                        
                        CustomProtocolMessage message = CreateFragment(bytes, i, fragmentSize);
                        message.SequenceNumber = seqNum;
                        message.Id = id;
                     
                        Console.WriteLine(Encoding.ASCII.GetString(message.Data));
                       
                        if(message.Last)
                        {
                            Console.WriteLine("last fragment");
                        }
                        //Console.WriteLine(Encoding.ASCII.GetString(message.Data));

                        fragmentsToSend[currentFragmentListIndex].Add(message);

                        if(seqNum == 20)
                        {
                            fragmentsToSend.Add(new List<CustomProtocolMessage>());
                            
                            currentFragmentListIndex+=1;
                            seqNum = 0;
                        }else
                        {
                            seqNum++;
                        }

                    }
                    currentFragmentListIndex = 0;
                    /*for(int i = currentWindowStart; i <= currentWindowEnd; i++)
                    {
                        _unAcknowledgedMessages[id].Add(fragmentsToSend[currentFragmentListIndex][i].SequenceNumber);
                        Console.WriteLine(Encoding.ASCII.GetString(fragmentsToSend[currentFragmentListIndex][i].Data));
                        
                        await _connection.SendMessage(fragmentsToSend[currentFragmentListIndex][i]);
                        //Console.WriteLine(Encoding.ASCII.GetString(fragmentsToSend[i].Data));
                    }*/
                    
                    //Console.WriteLine("first Window send");
                   /* while(currentWindowEnd < fragmentsToSend[currentFragmentListIndex].Count-1)
                    {
                      
                           
                        await WaitForFirstInWindow(fragmentsToSend[currentFragmentListIndex],id, (UInt32)currentWindowStart);
                        currentWindowStart+=1;
                        currentWindowEnd+=1;
                        
                        _unAcknowledgedMessages[id].Add(fragmentsToSend[currentFragmentListIndex][currentWindowEnd].SequenceNumber);
                        await _connection.SendMessage(fragmentsToSend[currentFragmentListIndex][currentWindowEnd], true);
                        Console.WriteLine(Encoding.ASCII.GetString(fragmentsToSend[currentFragmentListIndex][currentWindowEnd].Data));
                        
                        if(currentWindowEnd == 20)
                        {
                            currentFragmentListIndex+=1;
                        }

                    }*/
                    for(int i = 0; i < fragmentsToSend.Count; i++)
                    {
                       Console.WriteLine($"sending portion {i}");
                       await StartSendingFragments(fragmentsToSend[i], id);
                       currentFragmentListIndex++;
                    }
                    /*await WaitForFirstInWindow(fragmentsToSend[currentFragmentListIndex-1],id, (UInt32)currentWindowStart);

                    await WaitForFirstInWindow(fragmentsToSend[currentFragmentListIndex-1],id, (UInt32)currentWindowStart+1);
                    await WaitForFirstInWindow(fragmentsToSend[currentFragmentListIndex-1],id, (UInt32)currentWindowStart+2);
                    await WaitForFirstInWindow(fragmentsToSend[currentFragmentListIndex-1],id, (UInt32)currentWindowStart+3);*/
                    Console.WriteLine("Transporation ended");
                });
                
                
            }
        }
        private async Task StartSendingFragments(List<CustomProtocolMessage> currentFragmentsPortion, UInt16 id)
        {
            int currentWindowStart = 0;
            int currentWindowEnd = currentWindowStart+_windowSize-1;
            for(int i = currentWindowStart; i <= currentWindowEnd; i++)
            {
                _unAcknowledgedMessages[id].Add(currentFragmentsPortion[i].SequenceNumber);
              
                Console.WriteLine(currentFragmentsPortion[i].SequenceNumber);
                await _connection.SendMessage(currentFragmentsPortion[i]);
                Console.WriteLine(Encoding.ASCII.GetString(currentFragmentsPortion[i].Data));
            }
            while(currentWindowEnd < currentFragmentsPortion.Count-1)
            {
                
                    
                await WaitForFirstInWindow(currentFragmentsPortion,id, (UInt32)currentWindowStart);
                currentWindowStart+=1;
                currentWindowEnd+=1;
              
                
                _unAcknowledgedMessages[id].Add(currentFragmentsPortion[currentWindowEnd].SequenceNumber);
                Console.WriteLine(currentFragmentsPortion[currentWindowEnd].SequenceNumber);

                await _connection.SendMessage(currentFragmentsPortion[currentWindowEnd], true);
                Console.WriteLine(Encoding.ASCII.GetString(currentFragmentsPortion[currentWindowEnd].Data));
                
                

            }
            await WaitForFirstInWindow(currentFragmentsPortion,id, (UInt32)currentWindowStart);

            await WaitForFirstInWindow(currentFragmentsPortion,id, (UInt32)currentWindowStart+1);
            await WaitForFirstInWindow(currentFragmentsPortion,id, (UInt32)currentWindowStart+2);
            await WaitForFirstInWindow(currentFragmentsPortion,id, (UInt32)currentWindowStart+3);
        }
        private async Task WaitForFirstInWindow(List<CustomProtocolMessage> fragments,UInt16 id, UInt32 seqNum)
        {
            if(!_unAcknowledgedMessages[id].Contains(seqNum))
            {
                return;
            }
            int overralTime = 0;
            int currentTime = 0;
            await Task.Run(async()=>
            {
                while(true)
                {
                    await Task.Delay(1000);
                    if(!_unAcknowledgedMessages[id].Contains(seqNum))
                    {
                        return;
                    }
                    currentTime+=500;
                    overralTime+=500;
                    
                    if(currentTime > 500)
                    {
                        currentTime = 0;
                        await _connection.SendMessage(fragments[(int)seqNum]);
                        //Console.WriteLine($"Fragment {seqNum} was resend");
                       // Console.WriteLine(Encoding.ASCII.GetString(fragments[(int)seqNum].Data));

                    }
                    
                }
            });

        }
        public CustomProtocolMessage CreateFragment(byte[] bytes, UInt16 start, uint fragmentSize)
        {
            CustomProtocolMessage message = new CustomProtocolMessage();
            
            
            
            
        
            int end = (int)(start+fragmentSize);
        
            if(end > bytes.Length)
            {
                message.SetFlag(CustomProtocolFlag.Last, true);
            }
            message.Data = bytes.Take(new Range(start, end)).ToArray();
            return message;
        }

        public async Task Connect(ushort port, string address)
        {
            
            await _connection.Connect(port, address);
        
        }
        public async Task Disconnect()
        {
            Console.WriteLine("Disconnecting...");
            await _connection.Disconnect();
            Console.WriteLine("Disconnected");

            
        }

        


        
    }
}