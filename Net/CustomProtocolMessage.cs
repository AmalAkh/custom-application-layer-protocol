


using System.Collections;

namespace CustomProtocol.Net
{
    public enum CustomProtocolFlag
    {
        Ack, Syn, Last, Ping,Pong
    }
    public class CustomProtocolMessage
    {
        public UInt32 SequenceNumber;
        public UInt16 Id;
        public bool[] Flags;

        
        public UInt16 HeaderCheckSum;

        public  byte[] Data;
        

        public UInt16 DataCheckSum;

        public CustomProtocolMessage()
        {
            Flags = new bool[8];
            Data = new byte[1];
        }
    
        public void SetFlag(CustomProtocolFlag flag, bool value)
        {
            Flags[Convert.ToInt16(flag)] = value;
        
        }

        public byte[] ToByteArray()
        {
        
            

            

            //convert bools to bytes
            int power = 7;
            byte flagsByte = 0;
            foreach(bool flag in Flags)
            {
                flagsByte += Convert.ToByte(Convert.ToByte(Math.Pow(2,power)) * (flag ? 1: 0));
                power--;
            }


            CRC16 crc16 = new CRC16();


            HeaderCheckSum = crc16.Compute([..BitConverter.GetBytes(SequenceNumber), ..BitConverter.GetBytes(Id),flagsByte]);


            DataCheckSum = crc16.Compute(Data);

            byte[] bytes = [..BitConverter.GetBytes(SequenceNumber), ..BitConverter.GetBytes(Id),flagsByte, ..BitConverter.GetBytes(HeaderCheckSum) ,..Data, ..BitConverter.GetBytes(DataCheckSum)];
            if(BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            return  bytes;
            

            
        }
        public static CustomProtocolMessage FromBytes(byte[] bytes)
        {
            if(bytes.Length < 11)
            {
                throw new Exception("Message is too small");
            }
            CustomProtocolMessage message = new CustomProtocolMessage();
            if(BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }
            message.SequenceNumber = BitConverter.ToUInt32(new ReadOnlySpan<byte>(bytes, 0,4));
            message.Id = BitConverter.ToUInt16(new ReadOnlySpan<byte>(bytes, 4,2));
            
            BitArray bitArray = new BitArray(new byte[]{bytes[6]});
            bitArray.CopyTo(message.Flags, 0);
            Array.Reverse(message.Flags);

            message.HeaderCheckSum = BitConverter.ToUInt16(new ReadOnlySpan<byte>(bytes, 7,2));




            message.Data = bytes.Take(new Range(0, bytes.Length -2)).ToArray<byte>();
            message.DataCheckSum =  BitConverter.ToUInt16(new ReadOnlySpan<byte>(bytes, bytes.Length-2,2));
           
            
         
            return message;
        }

        

        
        
    }
}