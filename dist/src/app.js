"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
const express_1 = __importDefault(require("express"));
const morgan_1 = __importDefault(require("morgan"));
const cors_1 = __importDefault(require("cors"));
const helmet_1 = __importDefault(require("helmet"));
const NODE_ENV = process.env.NODE_ENV;
const app = (0, express_1.default)();
// Type declaration for morgan options
const morganOption = (NODE_ENV === "production") ? "tiny" : "common";
// Middleware
app.use((0, morgan_1.default)(morganOption)); // Type declaration for morgan
app.use((0, cors_1.default)());
app.use((0, helmet_1.default)());
// Routes
app.get("/", (req, res) => {
    res.send("Hello, ts!");
});
// Catch uncaught exceptions
process.on('uncaughtException', (error) => {
    console.error('Uncaught Exception:', error);
    process.exit(1); // Exit the process with a non-zero status code
});
// Catch unhandled promise rejections
process.on('unhandledRejection', (reason, promise) => {
    console.error('Unhandled Rejection:', reason);
    // You might want to handle promise rejections here or log them for analysis
});
// Error handling middleware
app.use(function errorHandler(err, req, res, next) {
    if (res.headersSent) {
        return next(err);
    }
    const customErr = err;
    const status = customErr.status || 500;
    const message = customErr.message || "Internal Server Error";
    res.status(status).json({ error: message });
});
exports.default = app;
