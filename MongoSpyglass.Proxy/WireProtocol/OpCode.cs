using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MongoSpyglass.Proxy.WireProtocol;

public enum OpCode : int
{
    // Wraps other opcodes using compression
    OP_COMPRESSED = 2012,

    // Send a message using the standard format. 
    // Used for both client requests and database replies.
    OP_MSG = 2013,

    // Reply to a client request. responseTo is set.
    // Deprecated in MongoDB 5.0. Removed in MongoDB 5.1.
    OP_REPLY = 1,

    // Update document.
    // Deprecated in MongoDB 5.0. Removed in MongoDB 5.1.
    OP_UPDATE = 2001,

    // Insert new document.
    // Deprecated in MongoDB 5.0. Removed in MongoDB 5.1.
    OP_INSERT = 2002,

    // Formerly used for OP_GET_BY_OID.
    RESERVED = 2003,

    // Query a collection.
    // Deprecated in MongoDB 5.0. Removed in MongoDB 5.1.
    OP_QUERY = 2004,

    // Get more data from a query. See Cursors.
    // Deprecated in MongoDB 5.0. Removed in MongoDB 5.1.
    OP_GET_MORE = 2005,

    // Delete documents.
    // Deprecated in MongoDB 5.0. Removed in MongoDB 5.1.
    OP_DELETE = 2006,

    // Deprecated in MongoDB 5.0. Removed in MongoDB 5.1.
    OP_KILL_CURSORS = 2007
}