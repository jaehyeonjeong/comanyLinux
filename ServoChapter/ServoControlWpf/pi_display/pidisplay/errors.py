"""HTTP 로 그대로 돌려줄 수 있는 오류: 메시지 + 상태 코드."""


class ApiError(Exception):
    def __init__(self, message: str, code: int = 400):
        super().__init__(message)
        self.code = code
